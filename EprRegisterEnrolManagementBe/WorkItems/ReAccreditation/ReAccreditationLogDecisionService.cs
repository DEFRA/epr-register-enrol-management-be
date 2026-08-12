using System.Security.Claims;
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
/// </summary>
internal sealed class ReAccreditationLogDecisionService(
    IWorkItemPersistence persistence,
    IWorkItemService engine,
    IReAccreditationApprovalService approvalService,
    ILogger<ReAccreditationLogDecisionService> logger) : IReAccreditationLogDecisionService
{
    private const string AssessmentStateId = "assessment-in-progress";
    private const string AwaitingDecisionStateId = "awaiting-decision";
    private const string SubmitForDecisionActionId = "submit-for-decision";
    private const string RejectActionId = "reject";
    private const string ApprovedStateId = "approved";
    private const string RejectedStateId = "rejected";
    private const string WithdrawnStateId = "withdrawn";

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
        }
        else
        {
            // The submit-for-decision hop, if it ran, has already been
            // persisted. That is survivable rather than corrupt: the item now
            // sits in 'awaiting-decision', which this method accepts as an
            // entry state, so replaying the caller's identical request
            // completes the decision.
            logger.LogWarning(
                "Log-decision for work item {WorkItemId} failed at the {Outcome} step ({FailureCode}); " +
                "the work item is in '{StateId}' and the call may be safely retried.",
                workItemId, targetStateId, result.FailureCode,
                result.WorkItem?.StateId ?? AwaitingDecisionStateId);
        }

        return result;
    }

    private static WorkItemActionResult? RequireActorIdentity(ClaimsPrincipal user) =>
        string.IsNullOrWhiteSpace(user.FindFirstValue("user:id"))
            ? WorkItemActionResult.Failure(
                WorkItemActionFailureCode.MissingActorIdentity,
                "Mutating this work item requires an authenticated end user; " +
                "the request did not include a 'user:id' claim.")
            : null;
}
