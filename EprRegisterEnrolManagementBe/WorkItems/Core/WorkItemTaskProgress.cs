namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Snapshot of a single task's status for a work item, as projected by the
/// engine for the work item's current state.
///
/// <see cref="Status"/> (epr-gl6) is the canonical lifecycle value;
/// <see cref="IsComplete"/> is retained for back-compat with consumers
/// that pre-date the richer status set and is always
/// <c>Status == WorkItemTaskStatus.Completed</c>.
/// </summary>
public sealed record WorkItemTaskProgress(
    string TaskId,
    string DisplayName,
    bool IsComplete,
    WorkItemTaskStatus Status);