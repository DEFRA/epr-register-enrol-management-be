namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-739: a reusable state machine — the states, their labels, the
/// transitions between them and the state a new work item starts in. A case
/// type references a workflow through <see cref="IWorkItemType.Workflow"/>,
/// so several types can follow one shared definition (see
/// <c>WorkItems/Workflows/StandardWorkflow</c>) and each type's workflow can
/// still change on its own by pointing it at a different definition.
///
/// The constructor checks the definition is self-consistent, so a broken
/// workflow fails at startup rather than when a caseworker first acts on it.
/// </summary>
public sealed class WorkItemWorkflow
{
    public WorkItemWorkflow(
        string id,
        WorkItemState initialState,
        IReadOnlyCollection<WorkItemState> states,
        IReadOnlyCollection<WorkItemTransition> transitions
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(initialState);
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(transitions);

        var stateIds = states.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!stateIds.Contains(initialState.Id))
        {
            throw new InvalidWorkItemTypeException(
                $"Workflow '{id}' starts in '{initialState.Id}', which it does not declare."
            );
        }

        foreach (var transition in transitions)
        {
            if (!stateIds.Contains(transition.FromStateId) || !stateIds.Contains(transition.ToStateId))
            {
                throw new InvalidWorkItemTypeException(
                    $"Workflow '{id}' transition '{transition.ActionId}' moves "
                        + $"'{transition.FromStateId}' -> '{transition.ToStateId}', "
                        + "which are not both declared states."
                );
            }
        }

        Id = id;
        InitialState = initialState;
        States = states;
        Transitions = transitions;
    }

    /// <summary>Stable identifier of the workflow (e.g. <c>"standard"</c>).</summary>
    public string Id { get; }

    /// <summary>The state a newly-ingested work item starts in.</summary>
    public WorkItemState InitialState { get; }

    /// <summary>
    /// Every state, in declaration order. The order is the status sort rank
    /// (see <see cref="WorkItemSort"/>).
    /// </summary>
    public IReadOnlyCollection<WorkItemState> States { get; }

    /// <summary>Allowed transitions across <see cref="States"/>.</summary>
    public IReadOnlyCollection<WorkItemTransition> Transitions { get; }
}