namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// A possible state of a work item. <see cref="IsTerminal"/> marks states from which
/// no further task progress is expected (for example "approved" or "rejected").
/// </summary>
public sealed record WorkItemState(string Id, string DisplayName, bool IsTerminal = false);