using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// A persisted work item. The framework owns the envelope (id, type, state,
/// timestamps, submitted-by, payload); modules describe what their payload
/// means via their <see cref="IWorkItemType"/> and operate on it via their
/// own service objects.
/// </summary>
public sealed class WorkItem
{
    [BsonId(IdGenerator = typeof(GuidGenerator))]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The <see cref="IWorkItemType.TypeId"/> this item is an instance of.</summary>
    public required string TypeId { get; init; }

    /// <summary>Current <see cref="WorkItemState.Id"/>. Set to the type's initial state on creation.</summary>
    public required string StateId { get; set; }

    /// <summary>UTC timestamp the work item was first accepted into the system.</summary>
    public DateTime SubmittedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// UTC timestamp of the last engine-driven mutation (task completion, state
    /// transition). Equal to <see cref="SubmittedAt"/> for a freshly-submitted
    /// item.
    /// </summary>
    public DateTime LastModifiedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Identifier of the upstream caller that submitted the item (CDP Cognito client id).</summary>
    public string? SubmittedBy { get; init; }

    /// <summary>
    /// Identifier of the user the work item is currently assigned to, or
    /// <c>null</c> when no one is assigned. Set via the assignment endpoints
    /// rather than directly by modules so the engine can enforce role-based
    /// rules consistently.
    /// </summary>
    public string? AssignedToId { get; set; }

    /// <summary>
    /// Human-readable name of the assignee (snapshotted at assignment time so
    /// list views do not need a separate user lookup). <c>null</c> when no one
    /// is assigned.
    /// </summary>
    public string? AssignedToName { get; set; }

    /// <summary>UTC timestamp the current assignment was made; <c>null</c> when unassigned.</summary>
    public DateTime? AssignedAt { get; set; }

    /// <summary>Identifier of the user who made the current assignment; <c>null</c> when unassigned.</summary>
    public string? AssignedBy { get; set; }

    /// <summary>
    /// Frozen copy of the type's template (states, tasks per state,
    /// transitions and version) captured at submission time. Used by the
    /// engine in preference to the live <see cref="IWorkItemType"/> so that
    /// the work item — and its audit history — keep rendering as they did at
    /// the time they were assessed, even when the live module's template
    /// changes later. Optional only to support legacy items submitted before
    /// versioning existed.
    /// </summary>
    public WorkItemTemplateSnapshot? TemplateSnapshot { get; set; }

    /// <summary>
    /// Convenience copy of <see cref="WorkItemTemplateSnapshot.TemplateVersion"/>
    /// so it can be queried/indexed without deserialising the whole snapshot.
    /// </summary>
    public string? TemplateVersion { get; set; }

    /// <summary>
    /// Ids of completed tasks, keyed by the state id those tasks belong to.
    /// Tracking per-state lets the engine reason about progress in the current
    /// state without losing the audit trail of work done in earlier states.
    /// </summary>
    public Dictionary<string, HashSet<string>> CompletedTaskIdsByState { get; init; } = new();

    /// <summary>
    /// Free-form, type-specific payload supplied by the upstream caller. Stored
    /// verbatim so modules can interpret it however they choose. Persisted as a
    /// BSON sub-document; the API converts to/from JSON at the boundary.
    /// </summary>
    public BsonDocument Payload { get; init; } = new();

    /// <summary>
    /// Append-only audit narrative attached to the work item by assessors
    /// (RA-96). Stored in insertion order; projected newest-first by the
    /// engine. Framework-owned so every type behaves identically.
    /// </summary>
    public List<WorkItemNote> Notes { get; init; } = new();

    /// <summary>
    /// Append-only system audit log (RA-97). The framework writes one entry
    /// here for every successful state-changing engine call (task
    /// completion, action application, assignment / unassignment, note
    /// added). Entries are stored in chronological (insertion) order and
    /// projected oldest-first on the wire so a UI renders a natural
    /// top-to-bottom timeline. Framework-owned so every work item type
    /// inherits the same audit behaviour without writing any audit code.
    /// </summary>
    public List<WorkItemAuditEntry> AuditLog { get; init; } = new();

    /// <summary>
    /// Optimistic concurrency token. Incremented by
    /// <see cref="IWorkItemPersistence.ReplaceAsync"/> on every successful
    /// save and used as a filter so two concurrent writers cannot silently
    /// overwrite one another's changes.
    /// </summary>
    public int Version { get; set; }
}