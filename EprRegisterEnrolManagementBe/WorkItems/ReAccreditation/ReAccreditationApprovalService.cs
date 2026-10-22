using System.Globalization;
using System.Security.Claims;
using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Utils.Background;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using ReAccreditationSlaClock = EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models.SlaClock;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-132 default <see cref="IReAccreditationApprovalService"/>. RA-527:
/// approval is two steps, each one write.
///
/// <see cref="RecordApprovedDeterminationAsync"/> (step 1, the determination)
/// stamps the accreditation number its caller minted, with its start date and
/// year, moves the item from <c>awaiting-decision</c> to the non-terminal
/// <c>approved-pending-contact</c>, and records the determination. Nothing is
/// applied.
///
/// <see cref="ApproveAsync"/> (step 2, "confirm applicant contact") applies
/// the outcome on a single <see cref="IWorkItemPersistence.ReplaceAsync"/>
/// call: it checks the item against
/// <see cref="ReAccreditationConfirmContactGuard"/> (pending contact, v17
/// rules, the step 1 number present — it is reused, never re-minted), stamps
/// <see cref="SlaClock.StoppedAt"/>, moves the item to <c>approved</c> and
/// appends the action-applied, <c>applicant-contacted</c>,
/// <c>sla-clock-stopped</c> and <c>accreditation-issued</c> audit entries.
/// After the write it enqueues a background job that appends a
/// <c>publishing-enqueued</c> audit entry via a scoped
/// <see cref="IWorkItemAuditAppender"/>, then invokes the registered
/// <see cref="IWorkItemPostActionHook"/>s with action id
/// <c>confirm-contact-approved</c> so the notification hook sends the
/// Decision email. RA-323: any authenticated caseworker may approve — there
/// is no separate decision-maker role.
///
/// On a <see cref="WorkItemConcurrencyException"/> either write is retried
/// by reloading the latest document and re-running validation, up to
/// <see cref="MaxAttempts"/> times — mirroring the
/// <c>WorkItemAuditAppender</c> retry pattern.
///
/// RA-448 phase 2: the accreditation id is not fabricated locally — it is
/// resolved from the real backend via <see cref="IAccreditationNumberAdapter"/>
/// (<see cref="ResolveAccreditationNumberAsync"/>), which returns a genuine,
/// DEFRA-public-register-format number.
/// </summary>
internal sealed class ReAccreditationApprovalService(
    IWorkItemPersistence persistence,
    IWorkItemRegistry registry,
    IAccreditationNumberAdapter accreditationNumberAdapter,
    IBackgroundTaskQueue backgroundTaskQueue,
    IEnumerable<IWorkItemPostActionHook> postActionHooks,
    ILogger<ReAccreditationApprovalService> logger,
    IOptions<AccreditationConfig> accreditationOptions,
    TimeProvider? timeProvider = null
) : IReAccreditationApprovalService
{
    private const string AccreditationIdKey = "accreditationId";

    private const int MaxAttempts = 3;
    private const string ToStateId = ReAccreditationType.ApprovedStateId;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IWorkItemPostActionHook[] _postActionHooks = postActionHooks.ToArray();
    private readonly AccreditationConfig _accreditationConfig = accreditationOptions.Value;

    public async Task<AccreditationNumberResult> ResolveAccreditationNumberAsync(
        Guid workItemId,
        Guid correlationId,
        CancellationToken cancellationToken = default
    )
    {
        var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return AccreditationNumberResult.Failure(
                $"No work item exists with id '{workItemId}'."
            );
        }

        return await ResolveAccreditationNumberForWorkItemAsync(
            workItem,
            _accreditationConfig.CurrentYear,
            correlationId,
            cancellationToken
        );
    }

    public async Task<WorkItemActionResult> ApproveAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        DateTimeOffset appliedAt,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(user);

        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        var appliedAtUtc = appliedAt.UtcDateTime;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);

            // Re-checked on every attempt, against the item as it is now: a
            // concurrent confirmation that landed first turns this into a
            // replay, and anything else that moved the item refuses it.
            if (
                ReAccreditationConfirmContactGuard.Check(workItemId, workItem, registry) is
                { } stop
            )
            {
                return stop;
            }

            var payload = TryStopSlaClock(workItem!, appliedAt);
            if (payload is null)
            {
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.InvalidTransition,
                    $"Work item '{workItemId}' payload is corrupt and cannot be read. "
                        + "Inspect the server logs for details; a manual data repair may be required."
                );
            }

            var previousState = workItem!.StateId;
            workItem.StateId = ToStateId;
            workItem.LastModifiedAt = appliedAtUtc;

            AppendAudit(
                workItem,
                "action-applied",
                "Action applied",
                user,
                appliedAtUtc,
                new()
                {
                    ["actionId"] = ReAccreditationType.ConfirmContactApprovedActionId,
                    ["actionDisplayName"] = ReAccreditationType.ConfirmContactDisplayName,
                    ["fromStateId"] = previousState,
                    ["toStateId"] = workItem.StateId,
                }
            );
            AppendAudit(
                workItem,
                "applicant-contacted",
                "Applicant contacted",
                user,
                appliedAtUtc,
                new() { ["outcome"] = "approved" }
            );
            AppendAudit(
                workItem,
                "sla-clock-stopped",
                "SLA clock stopped",
                user,
                appliedAtUtc,
                new() { ["stoppedAt"] = appliedAt.ToString("O") }
            );
            AppendAudit(
                workItem,
                "accreditation-issued",
                "Accreditation issued",
                user,
                appliedAtUtc,
                new()
                {
                    [AccreditationIdKey] = payload.AccreditationId,
                    ["startDate"] = payload.AccreditationStartDate?.ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture
                    ),
                    ["accreditationYear"] = payload.AccreditationYear?.ToString(
                        CultureInfo.InvariantCulture
                    ),
                }
            );

            try
            {
                await persistence.ReplaceAsync(workItem, cancellationToken);
            }
            catch (WorkItemConcurrencyException) when (attempt < MaxAttempts)
            {
                continue;
            }
            catch (WorkItemConcurrencyException)
            {
                logger.LogError(
                    "ReAccreditation approval for work item {WorkItemId} abandoned after {Attempts} attempts "
                        + "due to repeated concurrency conflicts.",
                    workItemId,
                    MaxAttempts
                );
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.ConcurrencyConflict,
                    $"Work item '{workItemId}' was modified concurrently. Reload the work item and retry."
                );
            }

            logger.LogInformation(
                "Re-accreditation work item {WorkItemId} approved (applicant contacted) by {UserId}",
                workItem.Id,
                user.FindFirstValue("user:id")
            );

            await EnqueuePublishingAuditAsync(workItem, user, cancellationToken);
            await InvokeActionAppliedHooksAsync(
                workItem,
                ReAccreditationType.ConfirmContactApprovedActionId,
                previousState,
                user
            );

            return WorkItemActionResult.Success(workItem);
        }

        // Unreachable: the final attempt either returns success or the
        // ConcurrencyConflict failure above.
        throw new InvalidOperationException("Approval retry loop exited unexpectedly.");
    }

    public async Task<WorkItemActionResult> RecordApprovedDeterminationAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        AccreditationNumberResult accreditationNumber,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(accreditationNumber);

        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        if (
            !accreditationNumber.IsSuccess
            || string.IsNullOrWhiteSpace(accreditationNumber.AccreditationNumber)
        )
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.AccreditationNumberUnavailable,
                $"Work item '{workItemId}' could not be approved: no accreditation number was supplied."
            );
        }

        var accreditationId = accreditationNumber.AccreditationNumber;
        var accreditationYear = _accreditationConfig.CurrentYear;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);
            if (
                ValidateStepOneTarget(workItemId, workItem) is { } validationResult
            )
            {
                return validationResult;
            }

            var previousState = workItem!.StateId;
            var now = _timeProvider.GetUtcNow();
            var nowUtc = now.UtcDateTime;
            var accreditationStartDate = ComputeAccreditationStartDate(accreditationYear, nowUtc);

            // The SLA clock is left running: the outcome is applied at step 2.
            var stamped = TryUpdatePayload(
                workItem,
                payload =>
                    payload with
                    {
                        AccreditationId = accreditationId,
                        AccreditationStartDate = accreditationStartDate,
                        AccreditationYear = accreditationYear,
                    }
            );
            if (stamped is null)
            {
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.InvalidTransition,
                    $"Work item '{workItemId}' payload is corrupt and cannot be read. "
                        + "Inspect the server logs for details; a manual data repair may be required."
                );
            }

            workItem.StateId = ReAccreditationType.ApprovedPendingContactStateId;
            workItem.LastModifiedAt = nowUtc;

            AppendAudit(
                workItem,
                "action-applied",
                "Action applied",
                user,
                nowUtc,
                new()
                {
                    ["actionId"] = ReAccreditationType.MakeDeterminationApprovedActionId,
                    ["actionDisplayName"] = ReAccreditationType.MakeDeterminationApprovedDisplayName,
                    ["fromStateId"] = previousState,
                    ["toStateId"] = workItem.StateId,
                }
            );
            AppendAudit(
                workItem,
                "determination-made",
                "Determination made: Approved",
                user,
                nowUtc,
                new()
                {
                    ["outcome"] = "approved",
                    [AccreditationIdKey] = accreditationId,
                }
            );

            try
            {
                await persistence.ReplaceAsync(workItem, cancellationToken);
            }
            catch (WorkItemConcurrencyException) when (attempt < MaxAttempts)
            {
                continue;
            }
            catch (WorkItemConcurrencyException)
            {
                logger.LogError(
                    "Step 1 approval for work item {WorkItemId} abandoned after {Attempts} attempts "
                        + "due to repeated concurrency conflicts.",
                    workItemId,
                    MaxAttempts
                );
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.ConcurrencyConflict,
                    $"Work item '{workItemId}' was modified concurrently. Reload the work item and retry."
                );
            }

            logger.LogInformation(
                "Re-accreditation work item {WorkItemId} determined as approved (pending applicant contact) by {UserId}",
                workItem.Id,
                user.FindFirstValue("user:id")
            );

            await InvokeActionAppliedHooksAsync(
                workItem,
                ReAccreditationType.MakeDeterminationApprovedActionId,
                previousState,
                user
            );
            return WorkItemActionResult.Success(workItem);
        }

        // Unreachable: the final attempt either returns success or the
        // ConcurrencyConflict failure above.
        throw new InvalidOperationException("Step 1 approval retry loop exited unexpectedly.");
    }

    /// <summary>
    /// RA-527: the guards for <see cref="RecordApprovedDeterminationAsync"/>.
    /// Returns a result to hand straight back (failure, or an idempotent replay
    /// for an item already pending contact) or null when the item may move.
    /// The step 1 transition is checked against the item's own frozen
    /// snapshot, so a case still on pre-v17 rules is refused until migrated.
    /// </summary>
    private WorkItemActionResult? ValidateStepOneTarget(Guid workItemId, WorkItem? workItem)
    {
        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."
            );
        }

        if (!string.Equals(workItem.TypeId, ReAccreditationType.Id, StringComparison.OrdinalIgnoreCase))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                $"Work item {workItemId} is of type '{workItem.TypeId}', not '{ReAccreditationType.Id}'."
            );
        }

        if (
            string.Equals(
                workItem.StateId,
                ReAccreditationType.ApprovedPendingContactStateId,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return WorkItemActionResult.IdempotentReplay(workItem);
        }

        if (
            string.Equals(workItem.StateId, ToStateId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(workItem.StateId, "rejected", StringComparison.OrdinalIgnoreCase)
            || string.Equals(workItem.StateId, "withdrawn", StringComparison.OrdinalIgnoreCase)
        )
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.TerminalState,
                $"Work item {workItemId} is in terminal state '{workItem.StateId}'; no actions are allowed."
            );
        }

        var template = WorkItemEngineRules.ResolveTemplate(workItem, registry);
        var declared = template?.Transitions.Any(t =>
            string.Equals(
                t.ActionId,
                ReAccreditationType.MakeDeterminationApprovedActionId,
                StringComparison.OrdinalIgnoreCase
            )
            && string.Equals(t.FromStateId, workItem.StateId, StringComparison.OrdinalIgnoreCase)
        );
        if (declared != true)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Action '{ReAccreditationType.MakeDeterminationApprovedActionId}' is not available for "
                    + $"work item {workItemId} in state '{workItem.StateId}' "
                    + $"(template '{workItem.TemplateVersion}')."
            );
        }

        return null;
    }

    /// <summary>
    /// RA-133: start date is 1 Jan of the configured year, OR today's date
    /// when approval happens after 1 Jan of that year (the accreditation
    /// cannot start in the past).
    /// </summary>
    private static DateOnly ComputeAccreditationStartDate(int accreditationYear, DateTime nowUtc)
    {
        var jan1 = new DateOnly(accreditationYear, 1, 1);
        var today = DateOnly.FromDateTime(nowUtc);
        return today > jan1 ? today : jan1;
    }

    /// <summary>
    /// epr-r9oy: the number-resolution step behind
    /// <see cref="ResolveAccreditationNumberAsync"/>. Never throws — every
    /// failure mode is a failed <see cref="AccreditationNumberResult"/>.
    /// </summary>
    private async Task<AccreditationNumberResult> ResolveAccreditationNumberForWorkItemAsync(
        WorkItem workItem,
        int accreditationYear,
        Guid correlationId,
        CancellationToken cancellationToken
    )
    {
        ReAccreditationPayload freshPayload;
        try
        {
            freshPayload = BsonSerializer.Deserialize<ReAccreditationPayload>(
                workItem.Payload ?? new BsonDocument()
            );
        }
        catch (Exception ex)
            when (ex is BsonSerializationException or FormatException or InvalidCastException)
        {
            logger.LogError(
                ex,
                "Re-accreditation approval for work item {WorkItemId} aborted: existing payload "
                    + "could not be deserialised to resolve the fields needed to request an "
                    + "accreditation number.",
                workItem.Id
            );
            return AccreditationNumberResult.Failure(
                "Work item payload is corrupt and cannot be read."
            );
        }

        if (
            freshPayload.Nation is not { } nation
            || string.IsNullOrWhiteSpace(freshPayload.OperatorOrganisationId)
            || string.IsNullOrWhiteSpace(freshPayload.OperatorApplicationId)
        )
        {
            logger.LogError(
                "Work item {WorkItemId} is missing data required to request an accreditation number "
                    + "(nation, operatorOrganisationId, or operatorApplicationId).",
                workItem.Id
            );
            return AccreditationNumberResult.Failure(
                "Work item is missing data required to request an accreditation number "
                    + "(nation, operatorOrganisationId, or operatorApplicationId)."
            );
        }

        // RA-475: a numeric operatorOrganisationId is a BONUS, never a
        // requirement. `operatorOrganisationId` is the ReEx organisation id
        // and is a UUID on every genuinely-submitted application; only the
        // seeded fixtures carry numeric ones. Requiring it to parse — which
        // is what RA-448 phase 2 did — refused approval on every real
        // application, and because the refusal was an InvalidTransition it
        // reached the caseworker as management-fe's "this application has
        // changed since you opened it, refresh and try again" on a
        // determination no refresh could ever fix.
        //
        // The number's {OrgId:D6} segment is resolved by the backend from
        // ReEx's own organisation document, so null here is correct and
        // expected; a parsed value is passed through only as the backend's
        // fallback for organisations ReEx has no record of.
        var orgId = int.TryParse(
            freshPayload.OperatorOrganisationId,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsedOrgId
        )
            ? parsedOrgId
            : (int?)null;

        // RA-448 phase 2 review: OperatorApplicationId is the backend's
        // {applicationId} route segment — confirmed (not assumed) against
        // HttpCaseWorkingApiAdapter.BuildPayload in epr-register-enrol-backend,
        // which sends operatorApplicationId = application.ApplicationId (the
        // AccreditationApplicationModel's own Mongo id) on every real
        // submission. OperatorRegistrationId, used here before the fix, is a
        // different value — the seed-time ReEx registration id — and every
        // other route on that backend's accreditation-applications group keys
        // on the document id, not the registration id.
        //
        // Regenerate=false, not true: a number is minted at most once per
        // application (step 1 of an approval; step 2 reuses it), so
        // the only time this call runs twice for the SAME application is a
        // retry after a transient failure — attempt 1 may have already
        // committed a number the caller never saw acknowledged. Regenerate=true
        // would ask the backend to bump/reissue on that retry, silently
        // changing an externally-quoted number; Regenerate=false returns the
        // already-issued one unchanged when the backend already has one, and
        // still generates on a genuine first call (see
        // IAccreditationNumberAdapter's contract) when it doesn't.
        var numberResult =
            await accreditationNumberAdapter.GenerateOrUpdateAccreditationNumberAsync(
                new AccreditationNumberRequest(
                    freshPayload.OperatorOrganisationId,
                    freshPayload.OperatorApplicationId,
                    nation,
                    orgId,
                    accreditationYear,
                    Regenerate: false,
                    CorrelationId: correlationId
                ),
                cancellationToken
            );

        if (!numberResult.IsSuccess)
        {
            logger.LogError(
                "Failed to resolve an accreditation number for work item {WorkItemId} "
                    + "(correlation {CorrelationId}): {Error}",
                workItem.Id,
                correlationId,
                numberResult.ErrorMessage
            );
        }

        return numberResult;
    }

    /// <summary>
    /// Applies <paramref name="update"/> to the work item's payload and
    /// returns the updated model, or null (logged) when the stored payload
    /// cannot be read — writing over it would destroy existing payload data
    /// (organisation name, registration number, nation, etc.).
    /// </summary>
    private ReAccreditationPayload? TryUpdatePayload(
        WorkItem workItem,
        Func<ReAccreditationPayload, ReAccreditationPayload> update
    )
    {
        // Deserialise → with-mutate → reserialise so the mongo-driver's
        // serializer (camelCase convention, nested record) is the single
        // source of truth for the on-disk shape.
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
                "Re-accreditation approval for work item {WorkItemId} aborted: "
                    + "existing payload could not be deserialised. Approving would destroy "
                    + "existing payload data (organisation name, registration number, nation, etc.).",
                workItem.Id
            );
            return null;
        }

        var updated = update(payload);

        // RA-249: merge the modelled updates back over the *existing* payload rather
        // than replacing it wholesale. A full replace against this
        // [BsonIgnoreExtraElements] model dropped every unmodelled payload key
        // (applicationReference, source, siteAddress*, ...) on approval, which turned
        // the application ref into the work-item Guid downstream.
        // Note: Merge is a shallow top-level merge — an unmodelled key nested
        // *inside* a modelled top-level object would still be lost (that object is
        // round-tripped through the model before overwriting). All keys at risk today
        // (applicationReference, source, siteAddress*) are top-level, so they survive.
        var merged = (workItem.Payload ?? new BsonDocument()).DeepClone().AsBsonDocument;
        merged.Merge(updated.ToBsonDocument(), overwriteExistingElements: true);
        workItem.ReplacePayload(merged);
        return updated;
    }

    /// <summary>RA-527 step 2: stops the payload's SLA clock at <paramref name="stoppedAt"/>.</summary>
    private ReAccreditationPayload? TryStopSlaClock(WorkItem workItem, DateTimeOffset stoppedAt) =>
        TryUpdatePayload(
            workItem,
            payload => payload with { SlaClock = new ReAccreditationSlaClock(stoppedAt) }
        );

    private async Task EnqueuePublishingAuditAsync(
        WorkItem workItem,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        // ClaimsPrincipal holds only in-memory state, so it is safe to
        // close over and hand to the background job; the queued
        // delegate runs on its own DI scope.
        var workItemId = workItem.Id;
        var accreditationIdValue = TryReadString(workItem.Payload, AccreditationIdKey);

        await backgroundTaskQueue.QueueAsync(
            async (scopedServices, ct) =>
            {
                var appender = scopedServices.GetRequiredService<IWorkItemAuditAppender>();
                await appender.AppendAsync(
                    workItemId,
                    action: "publishing-enqueued",
                    actionDisplayName: "Publishing enqueued",
                    details: new Dictionary<string, string?>
                    {
                        [AccreditationIdKey] = accreditationIdValue,
                    },
                    user,
                    ct
                );
            },
            cancellationToken
        );
    }

    private async Task InvokeActionAppliedHooksAsync(
        WorkItem workItem,
        string actionId,
        string fromStateId,
        ClaimsPrincipal user
    )
    {
    // The transition is already committed by the time hooks run, so a client
    // that gives up waiting (management-fe times out) must not cancel them:
    // the operator-backend push and its audit entries would be lost, leaving
    // this service and the operator disagreeing about the application's state.
        foreach (var hook in _postActionHooks)
        {
            try
            {
                await hook.OnActionAppliedAsync(
                    workItem,
                    actionId,
                    fromStateId,
                    user,
                    CancellationToken.None
                );
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Post-action transition hook {HookType} failed for work item {WorkItemId} action {ActionId}",
                    hook.GetType().FullName,
                    workItem.Id,
                    actionId
                );
            }
        }
    }

    private static void AppendAudit(
        WorkItem workItem,
        string action,
        string actionDisplayName,
        ClaimsPrincipal user,
        DateTime createdAt,
        Dictionary<string, string?> details
    )
    {
        workItem.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = action,
                ActionDisplayName = actionDisplayName,
                Details = details,
                CreatedAt = createdAt,
                CreatedBy = user.FindFirstValue("user:id"),
                CreatedByName = user.FindFirstValue("user:name"),
                // epr-rr9s: snapshot the state as of this event. Callers invoke
                // this AFTER any state mutation, so workItem.StateId is the
                // resulting post-mutation state for this entry.
                StateId = workItem.StateId,
            }
        );
    }

    private static WorkItemActionResult? RequireActorIdentity(ClaimsPrincipal user)
    {
        var id = user.FindFirstValue("user:id");
        if (!string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        return WorkItemActionResult.Failure(
            WorkItemActionFailureCode.MissingActorIdentity,
            "Mutating this work item requires an authenticated end user; "
                + "the request did not include a 'user:id' claim."
        );
    }

    private static string? TryReadString(BsonDocument? document, string fieldName)
    {
        if (document is null || !document.TryGetValue(fieldName, out var value))
        {
            return null;
        }
        return value.IsBsonNull ? null : value.ToString();
    }
}
