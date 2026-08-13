using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-410 default <see cref="IReAccreditationLogDecisionService"/>.
///
/// Before RA-410 recording a determination took two caller round-trips:
/// <c>submit-for-decision</c> to park the application in
/// <c>awaiting-decision</c>, then <c>approve</c> / <c>reject</c> to close it.
/// A failure between the two left the application in <c>awaiting-decision</c>
/// with a checklist the caseworker had no CTA to discharge. This service
/// collapses both hops into one call so that window does not exist.
///
/// <c>awaiting-decision</c> itself survives — it is referenced across the
/// backend, the frontend and existing Mongo documents, and deleting it would
/// need a data migration for no user-visible gain. What is gone is any reason
/// for a human to see it.
///
/// Resolution strategy mirrors <see cref="ReAccreditationContinueReviewService"/>:
/// the caller names an outcome, never an action id. Both
/// <c>submit-for-decision</c> and <c>reject</c> are declared
/// <see cref="WorkItemTransition.CallerInvocable"/> <c>false</c>, so this
/// service — calling <see cref="IWorkItemService.ApplyActionAsync"/> directly
/// with a server-computed action id — is the only route to either.
///
/// Approving delegates to <see cref="IReAccreditationApprovalService"/> rather
/// than reaching for the engine: that service owns accreditation-id issuance,
/// the SLA clock stop, the queued publishing job and the decision
/// notification, and bypassing it would silently drop all four.
///
/// epr-p86e / RA-410: the decision is gated on the operator-journey (OJ)
/// status push. The OJ push is fired ONCE here, as a pre-commit gate for the
/// final outcome, BEFORE either internal hop is persisted — and the
/// post-action <see cref="ReAccreditationStatusPushHook"/> is suppressed for
/// the three decision actions so the push happens exactly once. If OJ cannot
/// be reached within its retry budget the decision is abandoned with a 500 and
/// nothing is written, so the item stays exactly where it was rather than
/// stranding in <c>awaiting-decision</c> (the bug this change fixes: the old
/// post-commit hook pushed on BOTH hops, and when OJ hung the request's time
/// budget was exhausted, cancelling the token mid-approval). A disabled push
/// (<see cref="OperatorBackendPushResult.IsSkipped"/>) is a pass — decisions
/// must still work where the push is switched off.
///
/// The OJ push body is state-transition only (no accreditation id or anything
/// minted during approval), which is exactly why it can be sent before the
/// outcome is committed.
/// </summary>
internal sealed class ReAccreditationLogDecisionService(
    IWorkItemPersistence persistence,
    IWorkItemService engine,
    IReAccreditationApprovalService approvalService,
    IOperatorBackendPushAdapter pushAdapter,
    IWorkItemAuditAppender auditAppender,
    ILogger<ReAccreditationLogDecisionService> logger,
    TimeProvider? timeProvider = null) : IReAccreditationLogDecisionService
{
    private const string AssessmentStateId = "assessment-in-progress";
    private const string AwaitingDecisionStateId = "awaiting-decision";
    private const string SubmitForDecisionActionId = "submit-for-decision";
    private const string ApproveActionId = "approve";
    private const string RejectActionId = "reject";
    private const string ApprovedStateId = "approved";
    private const string RejectedStateId = "rejected";
    private const string WithdrawnStateId = "withdrawn";

    // epr-p86e / RA-410: the fromState reported to OJ on the decision push.
    //
    // DELIBERATE CONTRACT-MATCH — CONFIRM WITH THE OJ BACKEND OWNER.
    // The old post-commit push for approve/reject reported fromState
    // 'awaiting-decision' (the item HAD been persisted there by the first hop
    // before the push fired). Here the push fires BEFORE any hop is persisted,
    // so at push time the item is still in 'assessment-in-progress'. We keep
    // reporting 'awaiting-decision' to match the exact value OJ has always
    // received, so this change is invisible to the OJ contract. OJ is not in
    // the test stack and cannot be verified here — whoever owns the OJ backend
    // should confirm it does not care about (or actively expects) this value.
    private const string PushFromStateId = AwaitingDecisionStateId;

    private static readonly ReAccreditationType s_type = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<WorkItemActionResult> LogDecisionAsync(
        Guid workItemId,
        ReAccreditationDecisionOutcome outcome,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        // Checked here as well as by the engine so the caller gets a 401 for a
        // missing identity before any state is touched, rather than after the
        // first of the two hops has already landed.
        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);

        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'.");
        }

        if (!string.Equals(workItem.TypeId, ReAccreditationType.Id, StringComparison.OrdinalIgnoreCase))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                $"Work item {workItemId} is of type '{workItem.TypeId}', not '{ReAccreditationType.Id}'.");
        }

        var targetStateId = outcome == ReAccreditationDecisionOutcome.Approved
            ? ApprovedStateId
            : RejectedStateId;

        // A double-click, or a client retrying a response it never received,
        // must not fail. Only a replay of the SAME outcome is a no-op; an item
        // already closed the other way is a genuine conflict, because silently
        // succeeding would tell a caseworker their Refuse landed when the
        // application is in fact approved and an accreditation id is issued.
        if (string.Equals(workItem.StateId, targetStateId, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Log-decision for work item {WorkItemId} is a no-op: already in state '{StateId}'.",
                workItemId, workItem.StateId);
            return WorkItemActionResult.IdempotentReplay(workItem);
        }

        // Enumerated rather than read off the template, matching
        // ReAccreditationApprovalService: a decision must be refused on a
        // closed application even when its frozen snapshot is too old to
        // carry terminal metadata.
        if (string.Equals(workItem.StateId, ApprovedStateId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(workItem.StateId, RejectedStateId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(workItem.StateId, WithdrawnStateId, StringComparison.OrdinalIgnoreCase))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.TerminalState,
                $"Work item {workItemId} is in terminal state '{workItem.StateId}'; no decision can be recorded.");
        }

        // The two accepted entry states. 'awaiting-decision' is accepted not
        // for the frontend's benefit — it never sends an application there —
        // but so that an application stranded mid-hop by an earlier failure,
        // or left there by the pre-RA-410 two-step flow, is finished by the
        // identical call rather than needing a bespoke rescue path.
        var needsSubmitForDecision =
            string.Equals(workItem.StateId, AssessmentStateId, StringComparison.OrdinalIgnoreCase);

        if (!needsSubmitForDecision &&
            !string.Equals(workItem.StateId, AwaitingDecisionStateId, StringComparison.OrdinalIgnoreCase))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"A decision can only be recorded for a work item in '{AssessmentStateId}' or " +
                $"'{AwaitingDecisionStateId}', but {workItemId} is in '{workItem.StateId}'.");
        }

        // epr-p86e / RA-410 PRE-COMMIT OJ GATE. Notify the operator journey of
        // the final outcome BEFORE persisting either internal hop. On failure,
        // nothing has been written, so there is nothing to revert — the item
        // stays exactly where it was and the caller gets a generic 500.
        var actionId = outcome == ReAccreditationDecisionOutcome.Approved ? ApproveActionId : RejectActionId;
        var actionDisplayName = outcome == ReAccreditationDecisionOutcome.Approved ? "Approve" : "Reject";
        var toStateDisplayName = ResolveStateDisplayName(workItem, targetStateId);
        var correlationId = Guid.NewGuid();
        var occurredAt = _timeProvider.GetUtcNow().UtcDateTime;

        var pushResult = await pushAdapter.PushDecisionStatusChangedAsync(
            workItemId, correlationId, PushFromStateId, targetStateId, toStateDisplayName,
            actionId, actionDisplayName, occurredAt, cancellationToken);

        if (!pushResult.IsSuccess && !pushResult.IsSkipped)
        {
            logger.LogWarning(
                "Log-decision for work item {WorkItemId} abandoned: the operator journey could not be " +
                "notified (correlation {CorrelationId}): {ErrorMessage}. No state was changed.",
                workItemId, correlationId, pushResult.ErrorMessage);
            await AppendStatusPushAuditAsync(
                workItemId, "status-push-failed", "Status failed to send to OJ",
                BuildPushDetails(correlationId, actionId, actionDisplayName, targetStateId, toStateDisplayName,
                    extraKey: "errorMessage", extraValue: pushResult.ErrorMessage),
                user, cancellationToken);
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UpstreamNotificationFailed,
                "The re-accreditation decision could not be recorded because the operator journey could not " +
                "be notified. No change was made; try again shortly.");
        }

        if (needsSubmitForDecision)
        {
            var submitted = await engine.ApplyActionAsync(
                workItemId, SubmitForDecisionActionId, user, cancellationToken);

            if (!submitted.IsSuccess)
            {
                // Nothing has been written, so the caller can simply retry.
                logger.LogWarning(
                    "Log-decision for work item {WorkItemId} abandoned: could not apply " +
                    "'{ActionId}' ({FailureCode}).",
                    workItemId, SubmitForDecisionActionId, submitted.FailureCode);
                return submitted;
            }
        }

        var result = outcome == ReAccreditationDecisionOutcome.Approved
            ? await approvalService.ApproveAsync(workItemId, user, cancellationToken)
            : await engine.ApplyActionAsync(workItemId, RejectActionId, user, cancellationToken);

        if (result.IsSuccess)
        {
            logger.LogInformation(
                "Re-accreditation work item {WorkItemId} decided as {Outcome} by {UserId}",
                workItemId, targetStateId, user.FindFirstValue("user:id"));

            // Record the single decision push's outcome now the decision has
            // landed, mirroring the audit entries ReAccreditationStatusPushHook
            // used to emit for these actions (it no longer fires for them).
            // Appended after the transition so it sits after the action-applied
            // entries, exactly as the post-commit hook's entry did. A skipped
            // (disabled) push is recorded under its own non-alerting outcome.
            if (pushResult.IsSkipped)
            {
                await AppendStatusPushAuditAsync(
                    workItemId, "status-push-skipped", "Status not sent to OJ (disabled)",
                    BuildPushDetails(correlationId, actionId, actionDisplayName, targetStateId, toStateDisplayName,
                        extraKey: "reason", extraValue: pushResult.ErrorMessage),
                    user, cancellationToken);
            }
            else
            {
                await AppendStatusPushAuditAsync(
                    workItemId, "status-push-sent", "Status sent to OJ",
                    BuildPushDetails(correlationId, actionId, actionDisplayName, targetStateId, toStateDisplayName),
                    user, cancellationToken);
            }
        }
        else
        {
            // The submit-for-decision hop, if it ran, has already been
            // persisted. That is survivable rather than corrupt: the item now
            // sits in 'awaiting-decision', which this method accepts as an
            // entry state, so replaying the caller's identical request
            // completes the decision. OJ was already told the final outcome by
            // the gate above, so the replay does not re-notify it needlessly
            // beyond a single idempotent status upsert.
            logger.LogWarning(
                "Log-decision for work item {WorkItemId} failed at the {Outcome} step ({FailureCode}); " +
                "the work item is in '{StateId}' and the call may be safely retried.",
                workItemId, targetStateId, result.FailureCode,
                result.WorkItem?.StateId ?? AwaitingDecisionStateId);
        }

        return result;
    }

    /// <summary>
    /// The details bag recorded on a decision-push audit entry, matching the
    /// canonical key set ReAccreditationStatusPushHook writes (so management-fe's
    /// audit-log projection renders it identically) plus an optional extra key
    /// (<c>errorMessage</c> on a failure, <c>reason</c> on a skip).
    /// </summary>
    private static Dictionary<string, string?> BuildPushDetails(
        Guid correlationId, string actionId, string actionDisplayName,
        string toStateId, string toStateDisplayName,
        string? extraKey = null, string? extraValue = null)
    {
        var details = new Dictionary<string, string?>
        {
            ["actionId"] = actionId,
            ["actionDisplayName"] = actionDisplayName,
            ["fromStateId"] = PushFromStateId,
            ["toStateId"] = toStateId,
            ["toStateDisplayName"] = toStateDisplayName,
            ["correlationId"] = correlationId.ToString(),
        };
        if (extraKey is not null)
        {
            details[extraKey] = extraValue;
        }
        return details;
    }

    private async Task AppendStatusPushAuditAsync(
        Guid workItemId, string action, string actionDisplayName,
        Dictionary<string, string?> details, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var appended = await auditAppender.AppendAsync(
            workItemId, action, actionDisplayName, details, user, cancellationToken);
        if (!appended)
        {
            logger.LogWarning(
                "{Action} audit entry could not be persisted for work item {WorkItemId} (correlation {CorrelationId}).",
                action, workItemId, details.GetValueOrDefault("correlationId"));
        }
    }

    /// <summary>
    /// Resolves the human-readable label for <paramref name="stateId"/> from
    /// the work item's frozen template snapshot (preferred, so historical items
    /// keep their labels) or the live type as a fallback, falling back to the
    /// raw id — mirroring ReAccreditationStatusPushHook's own resolution so the
    /// decision push carries the same display name the hook used to send.
    /// </summary>
    private static string ResolveStateDisplayName(WorkItem workItem, string stateId)
    {
        IWorkItemTemplate template = (IWorkItemTemplate?)workItem.TemplateSnapshot ?? s_type;
        var displayName = template.States.FirstOrDefault(
            state => string.Equals(state.Id, stateId, StringComparison.OrdinalIgnoreCase))?.DisplayName;
        return displayName ?? stateId;
    }

    private static WorkItemActionResult? RequireActorIdentity(ClaimsPrincipal user) =>
        string.IsNullOrWhiteSpace(user.FindFirstValue("user:id"))
            ? WorkItemActionResult.Failure(
                WorkItemActionFailureCode.MissingActorIdentity,
                "Mutating this work item requires an authenticated end user; " +
                "the request did not include a 'user:id' claim.")
            : null;
}
