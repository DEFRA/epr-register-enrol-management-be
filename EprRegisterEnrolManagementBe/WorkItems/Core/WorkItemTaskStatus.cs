namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Lifecycle status of a single task on a work item, as projected by the
/// engine for the work item's current state. Introduced by epr-gl6 to
/// replace the legacy binary complete/incomplete view; legacy work items
/// that pre-date the per-task status map are projected as
/// <see cref="Completed"/> when present in
/// <see cref="WorkItem.CompletedTaskIdsByState"/> and
/// <see cref="NotStarted"/> otherwise.
///
/// Persisted as a string in BSON (registered in
/// <c>MongoConventions.Register</c>) so the on-disk shape is stable across
/// future enum value additions and remains human-readable in the database.
/// </summary>
public enum WorkItemTaskStatus
{
    NotStarted,
    InProgress,
    Blocked,
    Completed
}
