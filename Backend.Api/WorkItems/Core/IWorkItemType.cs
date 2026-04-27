namespace Backend.Api.WorkItems.Core;

/// <summary>
/// Declarative description of a work item type. Modules implement this to expose
/// their states and the tasks required while in each state. Implementations should
/// be pure, deterministic and side-effect free; they are queried by the framework
/// to advertise what a type can do, not to perform behaviour. Behaviour belongs in
/// the module's service objects.
/// </summary>
public interface IWorkItemType
{
    /// <summary>Stable, machine-readable identifier (e.g. "re-accreditation").</summary>
    string TypeId { get; }

    /// <summary>Human-readable name shown in UIs and audit logs.</summary>
    string DisplayName { get; }

    /// <summary>The state a newly-ingested work item starts in.</summary>
    WorkItemState InitialState { get; }

    /// <summary>All possible states this type can occupy.</summary>
    IReadOnlyCollection<WorkItemState> States { get; }

    /// <summary>
    /// Tasks required while the work item is in <paramref name="stateId"/>.
    /// May be computed dynamically per call to support data-driven flows.
    /// Returns an empty collection for states with no tasks.
    /// </summary>
    IReadOnlyCollection<WorkItemTask> GetTasksForState(string stateId);
}
