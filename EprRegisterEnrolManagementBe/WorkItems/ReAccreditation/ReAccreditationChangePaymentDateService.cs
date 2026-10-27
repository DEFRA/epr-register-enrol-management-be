using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-576 default <see cref="IReAccreditationChangePaymentDateService"/>.
///
/// Lets a caseworker correct the payment date entered at duly making. On a
/// single <see cref="IWorkItemPersistence.ReplaceAsync"/> it edits
/// <c>payload.paymentDate</c> and appends one <c>action-applied</c> audit entry.
/// It does NOT touch <see cref="WorkItem.StateId"/> or
/// <see cref="WorkItem.SlaClock"/> and runs no post-action hooks: the status and
/// the "Due on" date stay as they were (AC05), and nothing downstream reads the
/// payment date. The audit entry is appended in the same write rather than a
/// separate one, because <c>ReplaceAsync</c> is version-guarded and an out-of-band
/// append between load and save would make the save throw.
///
/// Concurrency is handled as in <see cref="ReAccreditationDulyMakingService"/>:
/// reload and re-validate on a <see cref="WorkItemConcurrencyException"/>, up to
/// <see cref="MaxAttempts"/> times.
/// </summary>
internal sealed class ReAccreditationChangePaymentDateService(
    IWorkItemPersistence persistence,
    IWorkItemRegistry registry,
    TimeProvider timeProvider,
    ILogger<ReAccreditationChangePaymentDateService> logger
) : IReAccreditationChangePaymentDateService
{
    private const int MaxAttempts = 3;
    private const string ActionId = "change-payment-date";
    private const string ActionDisplayName = "Change payment date";
    private const string DateFormat = "yyyy-MM-dd";

    public async Task<WorkItemActionResult> ChangePaymentDateAsync(
        Guid workItemId,
        DateOnly paymentDate,
        string reason,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(user);

        if (string.IsNullOrWhiteSpace(user.FindFirstValue("user:id")))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.MissingActorIdentity,
                "Mutating this work item requires an authenticated end user; "
                    + "the request did not include a 'user:id' claim."
            );
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);

            if (workItem is null)
            {
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.WorkItemNotFound,
                    $"No work item exists with id '{workItemId}'."
                );
            }

            if (
                !string.Equals(
                    workItem.TypeId,
                    ReAccreditationType.Id,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.UnknownAction,
                    $"Work item {workItemId} is of type '{workItem.TypeId}', "
                        + $"not '{ReAccreditationType.Id}'."
                );
            }

            var template = WorkItemEngineRules.ResolveTemplate(workItem, registry);
            if (template is null)
            {
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.UnknownAction,
                    $"Work item {workItemId} references unregistered type '{workItem.TypeId}' "
                        + "and has no stored template snapshot."
                );
            }

            // AC02: a determined or withdrawn application is closed. Queried and
            // updated items stay editable, and so does one with a logged decision
            // awaiting approval.
            if (
                template.States.Any(s =>
                    string.Equals(s.Id, workItem.StateId, StringComparison.OrdinalIgnoreCase)
                    && s.IsTerminal
                )
            )
            {
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.TerminalState,
                    $"Work item {workItemId} is in terminal state '{workItem.StateId}'; "
                        + "no actions are allowed."
                );
            }

            ReAccreditationPayload payload;
            try
            {
                payload = BsonSerializer.Deserialize<ReAccreditationPayload>(
                    workItem.Payload ?? new BsonDocument()
                );
            }
            catch (Exception ex)
                when (ex is BsonSerializationException or FormatException or InvalidCastException)
            {
                logger.LogError(
                    ex,
                    "Payment date change on work item {WorkItemId} aborted: existing payload "
                        + "could not be deserialised. Proceeding would destroy existing payload data.",
                    workItem.Id
                );
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.InvalidTransition,
                    $"Work item '{workItemId}' payload is corrupt and cannot be read. "
                        + "Inspect the server logs for details; a manual data repair may be required."
                );
            }

            // Items duly made without a payment date (auto-duly-made, or after
            // the remove-payment-received migration) have nothing to correct.
            if (payload.PaymentDate is not { } previousPaymentDate)
            {
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.InvalidTransition,
                    $"Work item '{workItemId}' has no payment date to change."
                );
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;

            // Merge rather than replace: the payload model ignores extra
            // elements, so a wholesale replace would drop every unmodelled key.
            var merged = (workItem.Payload ?? new BsonDocument()).DeepClone().AsBsonDocument;
            merged.Merge(
                (payload with { PaymentDate = paymentDate }).ToBsonDocument(),
                overwriteExistingElements: true
            );
            workItem.ReplacePayload(merged);
            workItem.LastModifiedAt = now;

            workItem.AuditLog.Add(
                new WorkItemAuditEntry
                {
                    Action = "action-applied",
                    ActionDisplayName = "Action applied",
                    Details = new Dictionary<string, string?>
                    {
                        ["actionId"] = ActionId,
                        ["actionDisplayName"] = ActionDisplayName,
                        ["fromStateId"] = workItem.StateId,
                        ["toStateId"] = workItem.StateId,
                        ["previousPaymentDate"] = previousPaymentDate.ToString(DateFormat),
                        ["paymentDate"] = paymentDate.ToString(DateFormat),
                        ["reason"] = reason,
                    },
                    CreatedAt = now,
                    CreatedBy = user.FindFirstValue("user:id"),
                    CreatedByName = user.FindFirstValue("user:name"),
                    StateId = workItem.StateId,
                }
            );

            try
            {
                await persistence.ReplaceAsync(workItem, cancellationToken);
            }
            catch (WorkItemConcurrencyException)
            {
                if (attempt == MaxAttempts)
                {
                    logger.LogError(
                        "Payment date change on work item {WorkItemId} abandoned after "
                            + "{Attempts} attempts due to repeated concurrency conflicts.",
                        workItemId,
                        MaxAttempts
                    );
                    return WorkItemActionResult.Failure(
                        WorkItemActionFailureCode.ConcurrencyConflict,
                        $"Work item '{workItemId}' was modified concurrently. "
                            + "Reload the work item and retry."
                    );
                }
                continue;
            }

            logger.LogInformation(
                "Payment date of re-accreditation work item {WorkItemId} changed by {UserId} "
                    + "from {PreviousPaymentDate} to {PaymentDate}.",
                workItem.Id,
                user.FindFirstValue("user:id"),
                previousPaymentDate.ToString(DateFormat),
                paymentDate.ToString(DateFormat)
            );
            return WorkItemActionResult.Success(workItem);
        }

        // Unreachable: the final attempt either returns or throws.
        throw new InvalidOperationException("Payment date change loop exited unexpectedly.");
    }
}
