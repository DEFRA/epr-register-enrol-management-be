using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-252 default <see cref="IReAccreditationWithdrawService"/>.
///
/// Deliberately thin, mirroring <see cref="ReAccreditationQueryService"/>:
/// the state change itself goes through the framework engine
/// (<see cref="IWorkItemService.ApplyActionAsync"/>) so state validation,
/// the generic <c>action-applied</c> audit entry, and post-action hooks
/// (including the Notify "Withdrawn" email) all behave exactly as they do
/// for any other transition. The bespoke parts are (a) resolving which
/// <c>withdraw</c>/<c>withdraw-during-*</c> action applies to the item's
/// current state — a direct current-state lookup, unlike
/// <see cref="ReAccreditationResumeService"/>'s audit-history resolution,
/// since withdraw needs no "which state did this leave from" memory — and
/// (b) recording the operator's reason as a work item note BEFORE the
/// transition, so <c>ReAccreditationNotificationHook</c>'s
/// <c>((withdrawal_notes))</c> placeholder (which reads the latest note)
/// picks it up.
///
/// Write order is note → transition, mirroring
/// <see cref="ReAccreditationQueryService"/>'s stamp-before-transition
/// convention for the same reason: the notification hook runs as a
/// post-action hook *inside* <see cref="IWorkItemService.ApplyActionAsync"/>.
/// </summary>
internal sealed class ReAccreditationWithdrawService(
    IWorkItemPersistence persistence,
    IWorkItemService engine,
    ILogger<ReAccreditationWithdrawService> logger
) : IReAccreditationWithdrawService
{
    /// <summary>
    /// Which withdraw transition applies in which state. Every non-terminal
    /// state has exactly one way out to <c>withdrawn</c> — kept next to the
    /// transitions declared on <see cref="ReAccreditationType"/> in spirit,
    /// mirroring <see cref="ReAccreditationQueryService"/>'s
    /// state→query-action map.
    /// </summary>
    private static readonly Dictionary<string, string> s_withdrawActionByState = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["submitted"] = "withdraw",
        ["duly-made"] = "withdraw-during-duly-made",
        ["assessment-in-progress"] = "withdraw-during-assessment",
        ["awaiting-decision"] = "withdraw-during-decision",
        ["queried"] = "withdraw-during-query",
        ["updated"] = "withdraw-during-updated",
    };

    /// <summary>
    /// Resolve the withdraw action id for a state, or <c>null</c> when the
    /// state cannot be withdrawn from (already decided, or already
    /// withdrawn).
    /// </summary>
    public static string? ResolveWithdrawActionId(string? stateId) =>
        stateId is not null && s_withdrawActionByState.TryGetValue(stateId, out var actionId)
            ? actionId
            : null;

    public async Task<WorkItemActionResult> WithdrawAsync(
        Guid workItemId,
        string reason,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(user);

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
                $"Work item {workItemId} is of type '{workItem.TypeId}', not '{ReAccreditationType.Id}'."
            );
        }

        if (string.Equals(workItem.StateId, "withdrawn", StringComparison.OrdinalIgnoreCase))
        {
            // A genuinely concurrent/duplicate withdraw (e.g. a double-click)
            // must not fail the caller's retry.
            logger.LogInformation(
                "Withdraw of work item {WorkItemId} is a no-op: already withdrawn.",
                workItemId
            );
            return WorkItemActionResult.IdempotentReplay(workItem);
        }

        var actionId = ResolveWithdrawActionId(workItem.StateId);
        if (actionId is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item {workItemId} is in state '{workItem.StateId}' and cannot be withdrawn. "
                    + "An application can only be withdrawn before a final decision is recorded."
            );
        }

        // Record the reason BEFORE the transition — ReAccreditationNotificationHook
        // runs as a post-action hook *inside* ApplyActionAsync, so this is the
        // only point at which the reason can be made visible to the Withdrawn
        // email's ((withdrawal_notes)) placeholder.
        var noteResult = await engine.AddNoteAsync(workItemId, reason, user, cancellationToken);
        if (!noteResult.IsSuccess)
        {
            return noteResult;
        }

        var result = await engine.ApplyActionAsync(workItemId, actionId, user, cancellationToken);
        if (!result.IsSuccess)
        {
            return result;
        }

        logger.LogInformation(
            "Re-accreditation work item {WorkItemId} withdrawn by {UserId} via {ActionId}",
            workItemId,
            user.FindFirstValue("user:id"),
            actionId
        );

        // Re-read so the response carries the note the out-of-band write above
        // recorded against its own copy of the document.
        var refreshed = await persistence.GetByIdAsync(workItemId, cancellationToken);
        return refreshed is null ? result : WorkItemActionResult.Success(refreshed);
    }
}
