using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// A free-text note attached to a work item by an assessor (RA-96). Notes
/// form an append-only audit narrative — they are framework-level so every
/// work item type gets the same storage, ordering and rendering behaviour
/// without each module having to opt in. Newest-first ordering is applied
/// at projection time; storage order is preserved on disk.
/// </summary>
public sealed class WorkItemNote
{
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The note body. Rendered verbatim; templates must escape.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// UTC timestamp the note was added. Has no default initializer — the
    /// engine must stamp this from the injected <see cref="TimeProvider"/>
    /// so tests with a <c>FakeTimeProvider</c> are not silently undermined
    /// by a wallclock fallback.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Identifier of the author. Snapshotted at creation time from the BFF
    /// user header so the audit history survives a user being renamed or
    /// removed from the directory later.
    /// </summary>
    public string? CreatedBy { get; init; }

    /// <summary>
    /// Human-readable name of the author at the time the note was added, so
    /// list views do not need a separate user lookup.
    /// </summary>
    public string? CreatedByName { get; init; }

    /// <summary>
    /// Optional id of the task this note is scoped to (RA-129 / epr-cky).
    /// <c>null</c> means a work-item-level note (the historical default);
    /// when set the note was authored against a specific task on the work
    /// item's current state and is rendered inline against that task on
    /// the dedicated tasks page. The id references
    /// <see cref="WorkItemTask.Id"/> on the snapshot template; the note
    /// still lives embedded on the work-item document — no separate
    /// collection or index is introduced.
    /// </summary>
    public string? TaskId { get; init; }
}
