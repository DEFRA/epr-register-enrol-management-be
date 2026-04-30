namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// A unit of work that must be completed against a work item while it is in a
/// particular state. The framework only describes the contract — the engine that
/// drives task completion is delivered by RA-92.
/// </summary>
public sealed record WorkItemTask(string Id, string DisplayName);