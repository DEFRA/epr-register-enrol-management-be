namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Snapshot of a single task's completion state for a work item, as projected
/// by the engine for the work item's current state.
/// </summary>
public sealed record WorkItemTaskProgress(string TaskId, string DisplayName, bool IsComplete);
