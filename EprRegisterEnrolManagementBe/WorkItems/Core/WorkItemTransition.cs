namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Declares an allowed move between two <see cref="WorkItemState"/>s, exposed
/// as a named action (e.g. "approve", "reject"). Modules attach transitions to
/// their <see cref="IWorkItemType"/> so the engine can decide which actions a
/// caller may invoke for a work item in its current state.
/// </summary>
/// <param name="ActionId">Stable, machine-readable id of the action (e.g. "approve").</param>
/// <param name="DisplayName">Human-readable label shown in UIs and audit logs.</param>
/// <param name="FromStateId">State the work item must be in for the action to be allowed.</param>
/// <param name="ToStateId">State the work item moves to when the action succeeds.</param>
/// <param name="RequiresAllTasksComplete">
/// When <c>true</c> (the default) every task returned by
/// <see cref="IWorkItemType.GetTasksForState"/> for <see cref="FromStateId"/>
/// must be marked complete before the action is allowed. Set to <c>false</c>
/// for transitions that should always be available (e.g. "withdraw").
/// </param>
public sealed record WorkItemTransition(
    string ActionId,
    string DisplayName,
    string FromStateId,
    string ToStateId,
    bool RequiresAllTasksComplete = true);