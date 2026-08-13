namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// The shape the engine actually queries to decide what actions apply to
/// a work item. Implemented by both the live <see cref="IWorkItemType"/> (used
/// for new work items) and by <see cref="WorkItemTemplateSnapshot"/> (a frozen
/// copy stored on each work item when it is submitted, so that audit/history
/// rendering remains faithful even when a module's templates change later).
/// </summary>
public interface IWorkItemTemplate
{
    /// <summary>Stable, machine-readable identifier of the template version (e.g. "v1").</summary>
    string TemplateVersion { get; }

    /// <summary>All states this template recognises.</summary>
    IReadOnlyCollection<WorkItemState> States { get; }

    /// <summary>Allowed transitions across <see cref="States"/>.</summary>
    IReadOnlyCollection<WorkItemTransition> Transitions { get; }
}