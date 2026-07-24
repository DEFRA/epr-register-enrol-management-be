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

    /// <summary>
    /// Inverse of the <c>resume-during-*</c> action that put a work item
    /// into <c>updated</c>: which <c>continue-review-during-*</c> action
    /// carries it on to the state it was originally queried from.
    /// </summary>
    private static readonly Dictionary<string, string> s_continueActionByResumeAction =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["resume-during-duly-making"] = "continue-review-during-duly-making",
            ["resume-during-duly-made"] = "continue-review-during-duly-made",
            ["resume-during-assessment"] = "continue-review-during-assessment",
            ["resume-during-decision"] = "continue-review-during-decision",
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

        if (!string.Equals(workItem.StateId, "updated", StringComparison.OrdinalIgnoreCase))
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

        var resumeActionId = workItem
            .AuditLog
            .Where(entry => string.Equals(entry.Action, "action-applied", StringComparison.Ordinal))
            .OrderByDescending(entry => entry.CreatedAt)
            .Select(entry => entry.Details.GetValueOrDefault("actionId"))
            .FirstOrDefault(actionId => actionId is not null);

        if (resumeActionId is null
            || !s_continueActionByResumeAction.TryGetValue(resumeActionId, out var continueActionId))
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
