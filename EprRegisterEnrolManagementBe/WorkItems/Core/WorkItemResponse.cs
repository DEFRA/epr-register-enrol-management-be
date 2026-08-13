using System.Text.Json;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// API representation of a persisted work item. Mirrors <see cref="WorkItem"/>
/// but carries the payload as a JSON element so callers do not see BSON types,
/// and projects engine state (the actions the engine will currently allow) so
/// a UI can render without re-deriving it.
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
    // RA-410: the state this work item returns to when its current waypoint
    // discharges (see IWorkItemOriginStateResolver). Equal to StateId for any
    // item not in a waypoint state, so a client can read it unconditionally.
    //
    // Load-bearing rather than informational: for re-accreditation's 'updated'
    // state this is the state the query was raised from, which is derivable
    // only from the work item's own audit history. The case management
    // frontend uses it to decide which call to action to offer — most
    // importantly to offer "Duly make" for an application queried out of
    // 'submitted' while refusing one queried out of assessment or decision,
    // where offering it would send the application backwards. Without it that
    // journey has no server-side signal to key off and clients start
    // hardcoding module state ids.
    //
    // Additive + nullable, so the DTO stays backward-compatible.
    string? OriginStateId = null
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
