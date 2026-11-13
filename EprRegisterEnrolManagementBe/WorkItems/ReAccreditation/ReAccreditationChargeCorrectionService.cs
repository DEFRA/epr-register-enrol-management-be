using System.Globalization;
using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-766 (temporary) default <see cref="IReAccreditationChargeCorrectionService"/>.
///
/// Called by the operator backend's one-off correction migration to fix work
/// items whose charge was never updated after a query. On a single
/// <see cref="IWorkItemPersistence.ReplaceAsync"/> it sets
/// <c>payload.chargeAmountPence</c> and appends one <c>charge-corrected</c>
/// audit entry. It does NOT change <see cref="WorkItem.StateId"/> or the SLA
/// clock and runs no post-action hooks. Duly-made items are corrected too.
///
/// A closed application (see <see cref="IsClosed"/>) is never changed. The
/// payload is edited by setting the one field, so every other key is kept.
/// Concurrency is handled as in <see cref="ReAccreditationChangePaymentDateService"/>:
/// reload and re-check on a <see cref="WorkItemConcurrencyException"/>.
/// </summary>
internal sealed class ReAccreditationChargeCorrectionService(
    IWorkItemPersistence persistence,
    IWorkItemRegistry registry,
    TimeProvider timeProvider,
    ILogger<ReAccreditationChargeCorrectionService> logger
) : IReAccreditationChargeCorrectionService
{
    private const int MaxAttempts = 3;

    // A dedicated audit action, not `action-applied`: this is not a state
    // transition, and a transition-shaped entry would be mistaken for a resume
    // entry by ReAccreditationUpdatedOrigin.
    public const string AuditAction = "charge-corrected";
    public const string AuditActionDisplayName = "Charge corrected";
    public const string AuditReason = "RA-766";

    private const string PaymentDateField = "paymentDate";

    /// <summary>
    /// The states a charge is never corrected in: the template's terminal
    /// states (approved, rejected, withdrawn) plus the two pending-contact
    /// states, which are not terminal but are already a decided outcome.
    /// The one list; the operator backend relies on the 409 it produces.
    /// </summary>
    internal static bool IsClosed(IWorkItemTemplate template, string stateId) =>
        TerminalStates.Find(template, stateId) is not null
        || string.Equals(stateId, ReAccreditationType.ApprovedPendingContactStateId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(stateId, ReAccreditationType.RejectedPendingContactStateId, StringComparison.OrdinalIgnoreCase);

    public async Task<ChargeCorrectionResult> CorrectChargeAsync(
        Guid workItemId,
        int chargeAmountPence,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(user);

        if (string.IsNullOrWhiteSpace(user.FindFirstValue("user:id")))
        {
            return Refuse(
                WorkItemActionFailureCode.MissingActorIdentity,
                "Mutating this work item requires an authenticated end user; "
                    + "the request did not include a 'user:id' claim."
            );
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);

            var refusal = CheckCorrectable(workItemId, workItem);
            if (refusal is not null)
            {
                return refusal;
            }

            var previous = ReAccreditationResumeService.ReadChargeAmountPence(workItem!);
            var dulyMade = HasPaymentDate(workItem!);

            if (previous == chargeAmountPence)
            {
                return new ChargeCorrectionResult
                {
                    Outcome = ChargeCorrectionResult.Unchanged,
                    ChargeAmountPence = chargeAmountPence,
                    DulyMade = dulyMade,
                };
            }

            Apply(workItem!, previous, chargeAmountPence, dulyMade, user);

            try
            {
                await persistence.ReplaceAsync(workItem!, cancellationToken);
            }
            catch (WorkItemConcurrencyException) when (attempt < MaxAttempts)
            {
                continue;
            }
            catch (WorkItemConcurrencyException ex)
            {
                logger.LogError(
                    ex,
                    "Charge correction on work item {WorkItemId} abandoned after {Attempts} attempts "
                        + "due to repeated concurrency conflicts.",
                    workItemId,
                    MaxAttempts
                );
                return Refuse(
                    WorkItemActionFailureCode.ConcurrencyConflict,
                    $"Work item '{workItemId}' was modified concurrently. Reload the work item and retry."
                );
            }

            logger.LogInformation(
                "Charge of re-accreditation work item {WorkItemId} corrected by {UserId} "
                    + "from {PreviousChargeAmountPence} to {ChargeAmountPence} pence (duly made: {DulyMade}).",
                workItemId,
                user.FindFirstValue("user:id"),
                previous?.ToString(CultureInfo.InvariantCulture) ?? "none",
                chargeAmountPence,
                dulyMade
            );
            return new ChargeCorrectionResult
            {
                Outcome = ChargeCorrectionResult.Updated,
                ChargeAmountPence = chargeAmountPence,
                DulyMade = dulyMade,
            };
        }

        // Unreachable: the final attempt either returns or fails with a conflict.
        throw new InvalidOperationException("Charge correction loop exited unexpectedly.");
    }

    private ChargeCorrectionResult? CheckCorrectable(Guid workItemId, WorkItem? workItem)
    {
        if (workItem is null)
        {
            return Refuse(WorkItemActionFailureCode.WorkItemNotFound, $"No work item has id '{workItemId}'.");
        }

        if (!string.Equals(workItem.TypeId, ReAccreditationType.Id, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(
                WorkItemActionFailureCode.UnknownAction,
                $"Work item {workItemId} has type '{workItem.TypeId}', expected '{ReAccreditationType.Id}'."
            );
        }

        var template = WorkItemEngineRules.ResolveTemplate(workItem, registry);
        if (template is null)
        {
            return Refuse(
                WorkItemActionFailureCode.UnknownAction,
                $"Work item {workItemId} has no registered type or template snapshot."
            );
        }

        return IsClosed(template, workItem.StateId)
            ? Refuse(
                WorkItemActionFailureCode.TerminalState,
                $"Work item {workItemId} is closed ('{workItem.StateId}'); its charge cannot change."
            )
            : null;
    }

    private static ChargeCorrectionResult Refuse(WorkItemActionFailureCode code, string message) =>
        ChargeCorrectionResult.Refused(WorkItemActionResult.Failure(code, message));

    // Duly making is what stamps the payment date, so its presence is the marker.
    private static bool HasPaymentDate(WorkItem workItem) =>
        workItem.Payload is not null
        && workItem.Payload.TryGetValue(PaymentDateField, out var value)
        && !value.IsBsonNull;

    /// <summary>
    /// Mutates the loaded instance in memory only; the caller commits it with
    /// the single <c>ReplaceAsync</c>. Sets the one payload field, so every
    /// other key is kept.
    /// </summary>
    private void Apply(
        WorkItem workItem,
        int? previousChargeAmountPence,
        int chargeAmountPence,
        bool dulyMade,
        ClaimsPrincipal user
    )
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var payload = (workItem.Payload ?? new BsonDocument()).DeepClone().AsBsonDocument;
        payload[ReAccreditationResumeService.ChargeAmountPencePayloadField] = new BsonInt32(chargeAmountPence);
        workItem.ReplacePayload(payload);
        workItem.LastModifiedAt = now;

        workItem.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = AuditAction,
                ActionDisplayName = AuditActionDisplayName,
                Details = new Dictionary<string, string?>
                {
                    ["previousChargeAmountPence"] = previousChargeAmountPence?.ToString(CultureInfo.InvariantCulture),
                    ["chargeAmountPence"] = chargeAmountPence.ToString(CultureInfo.InvariantCulture),
                    ["dulyMade"] = dulyMade ? "true" : "false",
                    ["reason"] = AuditReason,
                },
                CreatedAt = now,
                CreatedBy = user.FindFirstValue("user:id"),
                CreatedByName = user.FindFirstValue("user:name"),
                StateId = workItem.StateId,
            }
        );
    }
}
