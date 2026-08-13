namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// The framework rules that both the generic engine
/// (<see cref="WorkItemService"/>) and a module's own bespoke service object
/// have to agree on.
///
/// RA-346: extracted from <see cref="WorkItemService"/>'s private helpers so
/// that bespoke module endpoints which deliberately sit outside the generic
/// <c>POST /work-items/{id}/actions/{actionId}</c> path can reach the
/// identical rule instead of reinventing (and drifting from) it.
///
/// RA-410: the task-completeness rules that made up most of this class went
/// with the rest of the task framework. Template resolution stays here — it
/// is the one rule every path still has to agree on, and both the engine and
/// the module services call it.
/// </summary>
internal static class WorkItemEngineRules
{
    /// <summary>
    /// Pick the template the engine should reason about for a work item. The
    /// snapshot stored on the work item wins so that historical items keep
    /// their original action set even if the live type has since changed; the
    /// live type is used only as a fallback for legacy items submitted before
    /// snapshots existed.
    /// </summary>
    internal static IWorkItemTemplate? ResolveTemplate(
        WorkItem workItem,
        IWorkItemRegistry registry
    ) => workItem.TemplateSnapshot ?? (IWorkItemTemplate?)registry.Find(workItem.TypeId);
}
