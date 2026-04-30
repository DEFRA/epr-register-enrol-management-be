namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Frozen copy of an <see cref="IWorkItemType"/>'s template (states, tasks
/// per state, transitions and version) captured when a work item is first
/// submitted. Stored alongside the work item so that — even if the live
/// module's templates evolve later — the work item and its audit history
/// continue to render with the same task list, action set and template
/// version they were assessed against.
///
/// Snapshots are taken eagerly at submission rather than lazily on read so
/// that the frozen view survives the live module being unregistered or its
/// task definitions changing.
/// </summary>
public sealed class WorkItemTemplateSnapshot : IWorkItemTemplate
{
    public required string TemplateVersion { get; init; }

    public required IReadOnlyCollection<WorkItemState> States { get; init; }

    public required IReadOnlyCollection<WorkItemTransition> Transitions { get; init; }

    /// <summary>
    /// Tasks required while in each known state, captured at snapshot time.
    /// Stored as a plain dictionary so MongoDB serialises cleanly.
    /// </summary>
    public required Dictionary<string, List<WorkItemTask>> TasksByState { get; init; }

    public IReadOnlyCollection<WorkItemTask> GetTasksForState(string stateId)
    {
        if (stateId is not null && TasksByState.TryGetValue(stateId, out var tasks))
        {
            return tasks;
        }
        return Array.Empty<WorkItemTask>();
    }

    /// <summary>
    /// Build a snapshot from a live <see cref="IWorkItemType"/>. Walks every
    /// declared state to capture its task list so the snapshot is
    /// self-contained and does not need to call the live type again later.
    /// </summary>
    public static WorkItemTemplateSnapshot Capture(IWorkItemType type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var states = type.States.ToList();
        var tasksByState = new Dictionary<string, List<WorkItemTask>>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in states)
        {
            tasksByState[state.Id] = type.GetTasksForState(state.Id).ToList();
        }

        return new WorkItemTemplateSnapshot
        {
            TemplateVersion = type.TemplateVersion,
            States = states,
            Transitions = type.Transitions.ToList(),
            TasksByState = tasksByState
        };
    }
}