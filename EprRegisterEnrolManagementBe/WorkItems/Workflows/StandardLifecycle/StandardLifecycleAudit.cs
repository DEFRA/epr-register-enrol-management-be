using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-495 (from re-accreditation's decision writes): the pieces every
/// Standard-workflow lifecycle write shares: the actor check, the in-memory
/// audit entry and the post-action hook fan-out once the write has landed.
/// </summary>
internal static class StandardLifecycleAudit
{
    /// <summary>
    /// Appends an audit entry to <paramref name="workItem"/> in memory, to be
    /// persisted by the caller's single write. Call it AFTER any state change:
    /// the entry snapshots the item's state as of this event (epr-rr9s).
    /// </summary>
    public static void Append(
        WorkItem workItem,
        string action,
        string actionDisplayName,
        ClaimsPrincipal user,
        DateTime createdAt,
        Dictionary<string, string?> details
    ) =>
        workItem.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = action,
                ActionDisplayName = actionDisplayName,
                Details = details,
                CreatedAt = createdAt,
                CreatedBy = user.FindFirstValue("user:id"),
                CreatedByName = user.FindFirstValue("user:name"),
                StateId = workItem.StateId,
            }
        );

    /// <summary>
    /// A 401-shaped failure when <paramref name="user"/> carries no
    /// <c>user:id</c> claim, otherwise null.
    /// </summary>
    public static WorkItemActionResult? RequireActorIdentity(ClaimsPrincipal user) =>
        string.IsNullOrWhiteSpace(user.FindFirstValue("user:id"))
            ? WorkItemActionResult.Failure(
                WorkItemActionFailureCode.MissingActorIdentity,
                "Mutating this work item requires an authenticated end user; "
                    + "the request did not include a 'user:id' claim."
            )
            : null;

    /// <summary>
    /// Runs every post-action hook for a committed write. A failing hook is
    /// logged, never rethrown: the write has landed.
    /// </summary>
    public static async Task InvokeActionAppliedHooksAsync(
        IReadOnlyList<IWorkItemPostActionHook> hooks,
        WorkItem workItem,
        string actionId,
        string fromStateId,
        ClaimsPrincipal user,
        ILogger logger
    )
    {
        // The transition is already committed by the time hooks run, so a client
        // that gives up waiting (management-fe times out) must not cancel them:
        // the operator-backend push and its audit entries would be lost, leaving
        // this service and the operator disagreeing about the application's state.
        foreach (var hook in hooks)
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
}
