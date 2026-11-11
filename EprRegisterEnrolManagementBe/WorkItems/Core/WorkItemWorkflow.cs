namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-739: a reusable state machine — the states, their labels, the
/// transitions between them and the state a new work item starts in. A case
/// type references a workflow through <see cref="IWorkItemType.Workflow"/>,
/// so several types can follow one shared definition (see
/// <c>WorkItems/Workflows/StandardWorkflow</c>) and each type's workflow can
/// still change on its own by pointing it at a different definition.
/// <see cref="WorkItemRegistry"/> checks at startup that each type's
/// engine-facing states and transitions are its workflow's.
/// </summary>
/// <param name="Id">Stable identifier of the workflow (e.g. <c>"standard"</c>).</param>
/// <param name="InitialState">The state a newly-ingested work item starts in.</param>
/// <param name="States">
/// Every state, in declaration order. The order is the status sort rank (see
/// <see cref="WorkItemSort"/>).
/// </param>
/// <param name="Transitions">Allowed transitions across <paramref name="States"/>.</param>
public sealed record WorkItemWorkflow(
    string Id,
    WorkItemState InitialState,
    IReadOnlyCollection<WorkItemState> States,
    IReadOnlyCollection<WorkItemTransition> Transitions
);