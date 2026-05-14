using System.Text.Json;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// API representation of a persisted work item. Mirrors <see cref="WorkItem"/>
/// but carries the payload as a JSON element so callers do not see BSON types,
/// and projects engine state (current-state task progress and the actions the
/// engine will currently allow) so a UI can render without re-deriving it.
///
/// <see cref="TemplateVersion"/> exposes the version of the type's template
/// the work item was assessed against, so a UI can pick a matching detail
/// template for faithful historical rendering.
/// </summary>
public sealed record WorkItemResponse(
    Guid Id,
    string TypeId,
    string StateId,
    DateTime SubmittedAt,
    DateTime LastModifiedAt,
    string? SubmittedBy,
    string TemplateVersion,
    JsonElement Payload,
    IReadOnlyCollection<WorkItemTaskProgress> Tasks,
    IReadOnlyCollection<WorkItemTransition> AvailableActions,
    string? AssignedToId = null,
    string? AssignedToName = null,
    DateTime? AssignedAt = null,
    string? AssignedBy = null,
    IReadOnlyCollection<WorkItemNoteResponse>? Notes = null,
    IReadOnlyCollection<WorkItemAuditEntryResponse>? AuditLog = null);

/// <summary>
/// Wire shape for a single note attached to a work item (RA-96). Returned
/// newest-first as part of <see cref="WorkItemResponse.Notes"/> so a UI can
/// render the audit narrative without a second round-trip.
/// </summary>
public sealed record WorkItemNoteResponse(
    Guid Id,
    string Text,
    DateTime CreatedAt,
    string? CreatedBy,
    string? CreatedByName)
{
    /// <summary>
    /// Optional id of the task this note is scoped to (RA-129 / epr-cky).
    /// Kept as a separate init-only property — rather than appended to the
    /// positional constructor — so future task-scoped fields can be added
    /// without a binary-breaking change to the record's primary ctor.
    /// </summary>
    public string? TaskId { get; init; }
}

/// <summary>
/// Wire shape for a single audit log entry (RA-97). Returned in
/// chronological (oldest-first) order as part of
/// <see cref="WorkItemResponse.AuditLog"/> so a UI can render a top-to-
/// bottom timeline without re-sorting.
/// </summary>
public sealed record WorkItemAuditEntryResponse(
    Guid Id,
    string Action,
    string ActionDisplayName,
    IReadOnlyDictionary<string, string?> Details,
    DateTime CreatedAt,
    string? CreatedBy,
    string? CreatedByName);