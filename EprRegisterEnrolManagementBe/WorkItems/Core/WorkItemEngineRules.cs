namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// The handful of framework rules that both the generic engine
/// (<see cref="WorkItemService"/>) and a module's own bespoke service object
/// have to agree on: which template a work item is judged against, what
/// "task complete" means, and what a tasks-incomplete rejection looks like.
///
/// RA-346: extracted from <see cref="WorkItemService"/>'s private helpers so
/// that bespoke module endpoints which deliberately sit outside the generic
/// <c>POST /work-items/{id}/actions/{actionId}</c> path — most notably
/// <c>ReAccreditationApprovalService</c>, whose <c>approve</c> is not a
/// registered <see cref="WorkItemTransition"/> and therefore never met the
/// generic <see cref="WorkItemTransition.RequiresAllTasksComplete"/> gate —
/// can enforce the identical rule with the identical failure contract
/// instead of reinventing (and drifting from) it.
/// </summary>
internal static class WorkItemEngineRules
{
    /// <summary>
    /// Pick the template the engine should reason about for a work item. The
    /// snapshot stored on the work item wins so that historical items keep
    /// their original task list and action set even if the live type has
    /// since changed; the live type is used only as a fallback for legacy
    /// items submitted before snapshots existed.
    /// </summary>
    internal static IWorkItemTemplate? ResolveTemplate(
        WorkItem workItem,
        IWorkItemRegistry registry
    ) => workItem.TemplateSnapshot ?? (IWorkItemTemplate?)registry.Find(workItem.TypeId);

    /// <summary>
    /// Current lifecycle status of a single task.
    ///
    /// epr-gl6: <see cref="WorkItem.TaskStatusesByState"/> is the canonical
    /// source of truth; the legacy <see cref="WorkItem.CompletedTaskIdsByState"/>
    /// bucket is only consulted when no per-task status is recorded, so
    /// documents written before the map existed still read correctly.
    /// </summary>
    internal static WorkItemTaskStatus GetCurrentTaskStatus(
        WorkItem workItem,
        string stateId,
        string taskId
    )
    {
        if (
            workItem.TaskStatusesByState.TryGetValue(stateId, out var inner)
            && inner.TryGetValue(taskId, out var explicitStatus)
        )
        {
            return explicitStatus;
        }
        if (
            workItem.CompletedTaskIdsByState.TryGetValue(stateId, out var bucket)
            && bucket.Contains(taskId)
        )
        {
            return WorkItemTaskStatus.Completed;
        }
        return WorkItemTaskStatus.NotStarted;
    }

    /// <summary>
    /// True when the work item's current state still has at least one task
    /// that is not <see cref="WorkItemTaskStatus.Completed"/>. A state with
    /// no declared tasks is never blocking.
    /// </summary>
    internal static bool HasIncompleteTasks(IWorkItemTemplate template, WorkItem workItem)
    {
        var required = template.GetTasksForState(workItem.StateId);
        if (required.Count == 0)
        {
            return false;
        }
        // epr-08y: TaskStatusesByState is the canonical source of truth
        // (epr-gl6 / WorkItem.cs:99-110). Consult it first and only fall
        // back to the legacy CompletedTaskIdsByState bucket when no
        // per-task status is recorded for a task. Reading only the legacy
        // bucket would let a v2 module that writes only to the canonical
        // map silently transition past incomplete tasks.
        return required.Any(t =>
            GetCurrentTaskStatus(workItem, workItem.StateId, t.Id) != WorkItemTaskStatus.Completed
        );
    }

    /// <summary>
    /// The single tasks-incomplete rejection used across the framework.
    /// Returns <c>null</c> when the action may proceed, so callers can write
    /// <c>if (RequireAllTasksComplete(...) is { } failure) return failure;</c>
    /// as their first guard, before any side effects.
    ///
    /// The message is deliberately identical whichever path produced it —
    /// the management-fe maps a single string, and an approve rejected for
    /// pending tasks must be indistinguishable from a generic action
    /// rejected for the same reason.
    /// </summary>
    internal static WorkItemActionResult? RequireAllTasksComplete(
        IWorkItemTemplate template,
        WorkItem workItem,
        string actionId
    ) =>
        HasIncompleteTasks(template, workItem)
            ? WorkItemActionResult.Failure(
                WorkItemActionFailureCode.IncompleteTasks,
                $"Action '{actionId}' requires every task for state '{workItem.StateId}' to be complete first."
            )
            : null;
}
