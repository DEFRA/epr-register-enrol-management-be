namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Declarative description of a work item type. Modules implement this to expose
/// their states and the tasks required while in each state. Implementations should
/// be pure, deterministic and side-effect free; they are queried by the framework
/// to advertise what a type can do, not to perform behaviour. Behaviour belongs in
/// the module's service objects.
/// </summary>
public interface IWorkItemType : IWorkItemTemplate
{
    /// <summary>Stable, machine-readable identifier (e.g. "re-accreditation").</summary>
    string TypeId { get; }

    /// <summary>Human-readable name shown in UIs and audit logs.</summary>
    string DisplayName { get; }

    /// <summary>The state a newly-ingested work item starts in.</summary>
    WorkItemState InitialState { get; }

    /// <summary>
    /// Stable identifier for the current shape of this type's templates and
    /// task definitions. Bump this whenever a change to <see cref="States"/>,
    /// <see cref="IWorkItemTemplate.GetTasksForState"/> or
    /// <see cref="Transitions"/> would render historical work items
    /// inconsistently. Frontends use the same identifier to pick a matching
    /// detail template, so historical items keep their original look.
    /// Defaults to <c>"v1"</c> so types delivered before versioning existed
    /// continue to compile.
    /// </summary>
    string IWorkItemTemplate.TemplateVersion => "v1";

    /// <summary>
    /// Allowed state transitions, exposed as named actions (e.g. "approve",
    /// "reject"). The engine consults this list to decide whether an action
    /// invoked by a caller is permitted given the work item's current state
    /// and outstanding tasks. Defaults to an empty list so types delivered
    /// before the engine existed continue to compile.
    /// </summary>
    IReadOnlyCollection<WorkItemTransition> IWorkItemTemplate.Transitions => Array.Empty<WorkItemTransition>();
}