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
/// with the rest of the task framework. Template and transition resolution
/// stay here — they are the rules every path still has to agree on.
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

    /// <summary>
    /// RA-351: the transition <paramref name="actionId"/> resolves to for a
    /// work item in <paramref name="stateId"/>. An action id can be declared on
    /// more than one state (e.g. <c>sla-extend</c> self-loops on both
    /// <c>assessment-in-progress</c> and <c>queried</c>), so the declaration
    /// whose from-state matches wins; matching on action id alone would pick
    /// the first declaration and wrongly reject the action from every other
    /// state it is valid in. Otherwise the first declaration is returned, so a
    /// caller can still name a valid from-state when rejecting the action.
    /// <c>null</c> when the template does not declare the action at all. Both
    /// ids match case-insensitively.
    ///
    /// RA-000: shared by the engine (<see cref="WorkItemService"/>) and the
    /// generic action endpoint's caller-invocable guard, so the guard always
    /// checks the transition the engine would apply.
    /// </summary>
    internal static WorkItemTransition? ResolveTransition(
        IWorkItemTemplate template,
        string stateId,
        string actionId
    )
    {
        var actionTransitions = template
            .Transitions.Where(t =>
                string.Equals(t.ActionId, actionId, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        return actionTransitions.FirstOrDefault(t =>
                string.Equals(t.FromStateId, stateId, StringComparison.OrdinalIgnoreCase)
            ) ?? actionTransitions.FirstOrDefault();
    }
}
