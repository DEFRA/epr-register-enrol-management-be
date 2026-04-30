using System.Text.Json.Serialization;

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
/// The wire format is also serialised as the enum's name string
/// (PascalCase, e.g. <c>"InProgress"</c>) so HTTP consumers (the
/// management FE in particular) can round-trip the value without a
/// numeric mapping table.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkItemTaskStatus>))]
public enum WorkItemTaskStatus
{
    NotStarted,
    InProgress,
    Blocked,
    Completed
}
