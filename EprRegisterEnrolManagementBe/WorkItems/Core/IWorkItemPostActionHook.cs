using System.Security.Claims;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Module-supplied hook invoked by <see cref="WorkItemService"/> after a
/// successful submission or action transition. Modules use these to fire
/// side effects (e.g. send a notification, append a module-specific
/// audit entry) without core having to know anything about them.
///
/// Hooks must:
/// <list type="bullet">
///   <item>Be idempotent / safe to invoke once per successful state change.</item>
///   <item>Never throw — failures must be swallowed and recorded by the
///   hook itself (typically via an audit entry written through
///   <see cref="IWorkItemAuditAppender"/>) so a side-effect failure does
///   not unwind the originating mutation that has already persisted.</item>
///   <item>Only react to work items whose <see cref="WorkItem.TypeId"/>
///   matches the module they belong to.</item>
/// </list>
/// </summary>
public interface IWorkItemPostActionHook
{
    /// <summary>
    /// Fires after <see cref="IWorkItemService.SubmitAsync"/> has
    /// successfully created and persisted a new work item.
    /// </summary>
    Task OnSubmittedAsync(
        WorkItem workItem,
        ClaimsPrincipal user,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fires after <see cref="IWorkItemService.ApplyActionAsync"/> has
    /// successfully transitioned a work item.
    /// <paramref name="actionId"/> is the transition that was just
    /// applied; <paramref name="fromStateId"/> is the state the work
    /// item was in immediately before the transition.
    /// </summary>
    Task OnActionAppliedAsync(
        WorkItem workItem,
        string actionId,
        string fromStateId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken);
}
