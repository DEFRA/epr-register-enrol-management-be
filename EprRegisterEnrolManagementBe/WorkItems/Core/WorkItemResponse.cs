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
    IReadOnlyCollection<WorkItemAuditEntryResponse>? AuditLog = null,
    TimeSpan? SlaRemaining = null,
    WorkItemSlaState? SlaState = null,
    // RA-295: absolute SLA deadline (slaClock.StartedAt + TargetDuration) so the
    // case header can render "Due on: {date}" without re-deriving it from the
    // relative SlaRemaining countdown. Mirrors
    // WorkItemListItemResponse.SlaDueDate (RA-324) so the single-item and list
    // shapes agree. Null under the same condition as SlaState/SlaRemaining —
    // no SLA clock started yet — so a UI renders a dash rather than a bogus
    // date. Always reflects the current clock, so an SLA extend/override moves
    // it. Additive + nullable, so the DTO stays backward-compatible.
    DateTime? SlaDueDate = null,
    // RA-318: surfaced as a top-level field (mirroring payload.applicationReference)
    // so callers don't need to parse the payload JSON to obtain it.
    string? ApplicationReference = null,
    // RA-372: the id of the state whose checklist Tasks actually contains.
    // Normally equal to StateId, but a work item type may declare that another
    // state's tasks apply while an item passes through a waypoint state
    // (re-accreditation's 'updated' is the motivating case). Without this,
    // Tasks quietly stops describing StateId with no way for a client to
    // detect it — which pushes clients into hardcoding module state ids.
    // Additive + nullable, so the DTO stays backward-compatible.
    string? TaskStateId = null
);

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
    string? CreatedByName
);

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
    string? CreatedByName
);
