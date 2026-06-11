using System.Security.Claims;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Module-supplied hook invoked by <see cref="WorkItemService"/> after a task
/// status change results in ALL tasks for the current state being completed.
/// Modules use this to fire side effects that should only happen once every
/// task in a state is done — for example, automatically transitioning to the
/// next lifecycle state.
///
/// Hooks must follow the same contract as <see cref="IWorkItemPostActionHook"/>:
/// be idempotent, never throw, and only react to matching work item types.
/// </summary>
public interface IWorkItemPostTaskHook
{
    /// <summary>
    /// Fires after a task status change (via
    /// <see cref="IWorkItemService.SetTaskStatusAsync"/> or
    /// <see cref="IWorkItemService.CompleteTaskAsync"/>) results in every
    /// task for <paramref name="stateId"/> being marked
    /// <see cref="WorkItemTaskStatus.Completed"/>.
    ///
    /// <paramref name="stateId"/> is the state the work item was in when the
    /// final task was completed. The work item's
    /// <see cref="WorkItem.StateId"/> may have already changed by the time
    /// a second hook in the chain executes if an earlier hook applied a
    /// state transition.
    /// </summary>
    Task OnAllTasksCompletedAsync(
        WorkItem workItem,
        string stateId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken);
}