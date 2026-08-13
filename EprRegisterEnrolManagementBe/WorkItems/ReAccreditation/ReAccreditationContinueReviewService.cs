using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-337 default <see cref="IReAccreditationContinueReviewService"/>.
///
/// Moves a work item on from the non-terminal <c>updated</c> state — where
/// <see cref="ReAccreditationResumeService"/> leaves it once an operator has
/// resubmitted a queried application — back to the state it was originally
/// queried from, once a caseworker has reviewed the resubmission.
///
/// Mirrors <see cref="ReAccreditationResumeService"/>'s resolution strategy:
/// the caller never names an action. The correct
/// <c>continue-review-during-*</c> transition is derived from the work
/// item's own audit history — specifically the <c>resume-during-*</c>
/// action that most recently moved it into <c>updated</c> — so a caseworker
/// cannot accidentally send an application back to the wrong stage of the
/// workflow. Unlike resume, there is no extra data to capture: the generic
/// <c>action-applied</c> entry the framework engine writes already records
/// everything (actionId, fromStateId, toStateId), so no bespoke audit
/// append is needed here.
/// </summary>
internal sealed class ReAccreditationContinueReviewService(
    IWorkItemPersistence persistence,
    IWorkItemService engine,
    ILogger<ReAccreditationContinueReviewService> logger) : IReAccreditationContinueReviewService
{
    /// <summary>
    /// States a work item can validly be continued into. Used to tell a
    /// genuine "already continued" idempotent replay apart from a work item
    /// that has moved on to some other, unrelated state (which is a real
    /// conflict, not a replay).
    /// </summary>
    private static readonly IReadOnlySet<string> s_continueTargetStates =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "submitted", "duly-made", "assessment-in-progress", "awaiting-decision",
        };

    public async Task<WorkItemActionResult> ContinueReviewAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

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

        if (!string.Equals(
                workItem.StateId,
                ReAccreditationUpdatedOrigin.StateId,
                StringComparison.OrdinalIgnoreCase))
        {
            // A genuinely concurrent/duplicate call (e.g. a double-click)
            // must not fail the caller's retry — once the work item has left
            // 'updated' into a state continue-review could have put it in,
            // treat this as a no-op success rather than a conflict. Anything
            // else is a real conflict: this work item was never waiting on
            // this call.
            if (s_continueTargetStates.Contains(workItem.StateId))
            {
                logger.LogInformation(
                    "Continue-review for work item {WorkItemId} is a no-op: already in state '{StateId}'.",
                    workItemId, workItem.StateId);
                return WorkItemActionResult.IdempotentReplay(workItem);
            }

            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item {workItemId} is in state '{workItem.StateId}' and cannot be continued from review.");
        }

        // RA-410: shared with ReAccreditationOriginStateResolver so the action
        // that moves the item on and the origin state reported on the wire are
        // resolved from exactly the same audit history — a caseworker can
        // never be offered one state's call to action and be carried into a
        // different one.
        var continueActionId = ReAccreditationUpdatedOrigin.ResolveContinueActionId(workItem);

        if (continueActionId is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item {workItemId} is 'updated' but the resume action that put it there could not be " +
                "resolved from its audit history, so the matching continue-review action cannot be determined.");
        }

        var result = await engine.ApplyActionAsync(workItemId, continueActionId, user, cancellationToken);

        if (result.IsSuccess)
        {
            logger.LogInformation(
                "Re-accreditation work item {WorkItemId} continued from review by {UserId} via {ActionId}",
                workItemId, user.FindFirstValue("user:id"), continueActionId);
        }

        return result;
    }
}
