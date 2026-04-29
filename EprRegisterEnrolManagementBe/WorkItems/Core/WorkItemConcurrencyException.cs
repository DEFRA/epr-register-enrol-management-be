namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Thrown by <see cref="IWorkItemPersistence.ReplaceAsync"/> when the saved
/// version no longer matches the version that was loaded — i.e. another
/// caller mutated the work item first. The engine catches this and surfaces
/// it as <see cref="WorkItemActionFailureCode.ConcurrencyConflict"/> (HTTP
/// 409) so the caller can retry against the latest state.
/// </summary>
public sealed class WorkItemConcurrencyException(Guid workItemId, int expectedVersion)
    : Exception($"Work item '{workItemId}' was modified concurrently (expected version {expectedVersion}).")
{
    public Guid WorkItemId { get; } = workItemId;
    public int ExpectedVersion { get; } = expectedVersion;
}
