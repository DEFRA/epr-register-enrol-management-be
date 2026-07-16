using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// A single entry in a work item's audit log (RA-97). The framework appends
/// one of these for every successful state-changing engine call (task
/// completion, action application, assignment, unassignment, note
/// added) so modules inherit a complete audit trail without writing any audit code
/// themselves.
///
/// Author identity is snapshotted from the <see cref="System.Security.Claims.ClaimsPrincipal"/>
/// at write time so the audit narrative survives later directory changes.
/// Stored in insertion order on disk; projected in the same chronological
/// (oldest-first) order on the wire so a UI renders a natural top-to-bottom
/// timeline.
/// </summary>
public sealed class WorkItemAuditEntry
{
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Stable machine-readable identifier of the action that produced this
    /// entry (e.g. <c>task-completed</c>, <c>action-applied</c>,
    /// <c>assigned</c>, <c>unassigned</c>, <c>note-added</c>). Useful for
    /// filtering / styling without parsing the display name.
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// Human-readable description of the action, suitable for direct
    /// rendering by a UI (e.g. <c>Task completed</c>, <c>Action applied</c>).
    /// </summary>
    public required string ActionDisplayName { get; init; }

    /// <summary>
    /// Free-form contextual details — e.g. the task id, action id, the
    /// from/to states, the assignee id/name, the note id. Stored as a
    /// string-to-string dictionary so the wire shape is stable and trivially
    /// renderable; the framework decides what to put here per action.
    /// </summary>
    public Dictionary<string, string?> Details { get; init; } = new();

    /// <summary>
    /// UTC timestamp the entry was recorded. Has no default initializer — the
    /// engine must stamp this from the injected <see cref="TimeProvider"/>
    /// so tests with a <c>FakeTimeProvider</c> are not silently undermined
    /// by a wallclock fallback.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Identifier of the actor that performed the action. Snapshotted from
    /// the <c>user:id</c> claim, falling back to the Cognito client id when
    /// the call carries no end-user identity.
    /// </summary>
    public string? CreatedBy { get; init; }

    /// <summary>
    /// Human-readable name of the actor at the time the entry was recorded
    /// (<c>user:name</c> claim) so list views do not need a separate user
    /// lookup.
    /// </summary>
    public string? CreatedByName { get; init; }
}