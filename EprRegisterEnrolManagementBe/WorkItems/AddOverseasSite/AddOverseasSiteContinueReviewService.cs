using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

namespace EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;

/// <summary>
/// RA-698: moves an add-ORS work item on from <c>updated</c> once a caseworker
/// has reviewed the operator's query response.
/// </summary>
internal interface IAddOverseasSiteContinueReviewService
{
    /// <summary>
    /// Move the item out of <c>updated</c> back into whichever state it was
    /// queried from. The caller never supplies an action id: the
    /// <c>continue-review-during-*</c> transition is resolved from the item's
    /// own <c>resume-during-*</c> audit history. Idempotent: an item that has
    /// already left <c>updated</c> for a state a continue could have put it in
    /// succeeds as an idempotent replay.
    /// </summary>
    Task<WorkItemActionResult> ContinueReviewAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// RA-698 default <see cref="IAddOverseasSiteContinueReviewService"/>, the
/// same logic as <c>ReAccreditationContinueReviewService</c>: the generic
/// <c>action-applied</c> entry the engine writes already records the move, so
/// there is no bespoke audit entry.
/// </summary>
internal sealed class AddOverseasSiteContinueReviewService(
    IWorkItemPersistence persistence,
    IWorkItemService engine,
    ILogger<AddOverseasSiteContinueReviewService> logger
) : IAddOverseasSiteContinueReviewService
{
    public async Task<WorkItemActionResult> ContinueReviewAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(user);

        var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."
            );
        }

        if (!string.Equals(workItem.TypeId, AddOverseasSiteType.Id, StringComparison.OrdinalIgnoreCase))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                $"Work item {workItemId} is of type '{workItem.TypeId}', not '{AddOverseasSiteType.Id}'."
            );
        }

        if (!string.Equals(workItem.StateId, StandardUpdatedOrigin.StateId, StringComparison.OrdinalIgnoreCase))
        {
            // A duplicate call (a double-click) must not fail the retry; any
            // other state is a real conflict.
            if (StandardQueryActions.ContinueReviewTargetStates.Contains(workItem.StateId))
            {
                logger.LogInformation(
                    "Continue-review for work item {WorkItemId} is a no-op: already in state '{StateId}'.",
                    workItemId,
                    workItem.StateId
                );
                return WorkItemActionResult.IdempotentReplay(workItem);
            }

            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item {workItemId} is in state '{workItem.StateId}' and cannot be continued from review."
            );
        }

        var continueActionId = StandardUpdatedOrigin.ResolveContinueActionId(workItem);
        if (continueActionId is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item {workItemId} is 'updated' but the resume action that put it there could not be "
                    + "resolved from its audit history, so the matching continue-review action cannot be determined."
            );
        }

        var result = await engine.ApplyActionAsync(workItemId, continueActionId, user, cancellationToken);
        if (result.IsSuccess)
        {
            logger.LogInformation(
                "Add-ORS work item {WorkItemId} continued from review by {UserId} via {ActionId}",
                workItemId,
                user.FindFirstValue("user:id"),
                continueActionId
            );
        }

        return result;
    }
}
