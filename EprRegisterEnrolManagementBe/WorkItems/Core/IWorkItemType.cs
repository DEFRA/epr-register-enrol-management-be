namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Declarative description of a work item type. Modules implement this to expose
/// their states and the transitions between them. Implementations should
/// be pure, deterministic and side-effect free; they are queried by the framework
/// to advertise what a type can do, not to perform behaviour. Behaviour belongs in
/// the module's service objects.
///
/// RA-739: this is the single place a case type is configured. It declares the
/// display label (<see cref="DisplayName"/>), <see cref="Category"/>,
/// <see cref="InYearChangeCode"/>, <see cref="UsesSubmissionReference"/> and
/// <see cref="Workflow"/>, so adding a case type that follows an existing
/// workflow is a configuration change.
/// </summary>
public interface IWorkItemType : IWorkItemTemplate
{
    /// <summary>Stable, machine-readable identifier (e.g. "re-accreditation").</summary>
    string TypeId { get; }

    /// <summary>
    /// The case type's display label, shown in UIs and audit logs (e.g.
    /// "Re-accreditation").
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// RA-739: the group this type belongs to (see
    /// <see cref="WorkItemCategories"/>), so the list can filter on every type
    /// in the group without naming each one. In-year change types declare
    /// <see cref="WorkItemCategories.InYearChange"/>. Defaults to <c>null</c>:
    /// the type is its own group (re-accreditation).
    /// </summary>
    string? Category => null;

    /// <summary>
    /// RA-739: whether a submission of this type is given a
    /// <c>SUB-{yyyy}-{sequence}</c> submission reference shared by every work
    /// item it creates. Defaults to <c>false</c> (re-accreditation).
    /// </summary>
    bool UsesSubmissionReference => false;

    /// <summary>
    /// RA-739: the workflow this type follows. A type that follows a shared
    /// workflow returns it here (re-accreditation returns
    /// <c>StandardWorkflow.Definition</c>) and takes <see cref="InitialState"/>,
    /// <see cref="IWorkItemTemplate.States"/> and
    /// <see cref="IWorkItemTemplate.Transitions"/> from it. The default wraps
    /// the type's own states and transitions in a bespoke workflow named after
    /// the type, so a type that declares its state machine directly needs no
    /// change.
    /// </summary>
    WorkItemWorkflow Workflow => new(TypeId, InitialState, States, Transitions);

    /// <summary>The state a newly-ingested work item starts in.</summary>
    WorkItemState InitialState { get; }

    /// <summary>
    /// RA-492: the in-year change type code (for example <c>"ORS"</c>) when
    /// this type is an in-year change. A non-null code gives every submission
    /// an <c>IYC-{code}-{yyyy}-{sequence}</c> <c>applicationReference</c> (see
    /// <see cref="InYearChangeReferenceGenerator"/>). Defaults to <c>null</c>,
    /// which gives the AP format, so existing types need no change. The type
    /// declares its own code, so Core never lists module type ids.
    /// </summary>
    string? InYearChangeCode => null;

    /// <summary>
    /// Stable identifier for the current shape of this type's templates and
    /// Bump this whenever a change to <see cref="States"/> or
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
    /// Defaults to an empty list so types delivered
    /// before the engine existed continue to compile.
    /// </summary>
    IReadOnlyCollection<WorkItemTransition> IWorkItemTemplate.Transitions => Array.Empty<WorkItemTransition>();
}