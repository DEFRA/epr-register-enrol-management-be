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
    /// <paramref name="stateId"/> is the state whose checklist was just
    /// finished. The work item's <see cref="WorkItem.StateId"/> may have
    /// already changed by the time a second hook in the chain executes if an
    /// earlier hook applied a state transition.
    ///
    /// <para><strong>RA-372:</strong> <paramref name="stateId"/> is the
    /// <em>effective task state</em> (see
    /// <see cref="IWorkItemTaskStateResolver"/>), which is <em>not</em>
    /// necessarily <see cref="WorkItem.StateId"/> even on entry. When a module
    /// redirects an item's task list to another state — as re-accreditation
    /// does for its <c>updated</c> waypoint — this fires with the redirected
    /// state while the item is still sitting in the other one. Hooks written
    /// before RA-372 assumed the two were always equal.</para>
    ///
    /// <para>A hook that only reads <paramref name="stateId"/> is unaffected. A
    /// hook that <em>changes state</em> must not assume the item is already in
    /// <paramref name="stateId"/>: moving it straight on to the next state
    /// would traverse an edge the template does not declare, skip the state the
    /// item is actually in, and put an unmodelled from/to pair into the audit
    /// trail and any downstream push. Take the declared transition out of the
    /// current state first — see <c>ReAccreditationDulyMadeHook</c>, which
    /// discharges the <c>updated</c> waypoint through its declared
    /// continue-review edge before marking the application duly made.</para>
    /// </summary>
    Task OnAllTasksCompletedAsync(
        WorkItem workItem,
        string stateId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken);
}
