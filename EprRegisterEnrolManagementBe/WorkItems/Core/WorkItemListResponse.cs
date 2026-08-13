using System.Text.Json;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// API representation of a single page of work items returned by
/// <c>GET /work-items</c>. Mirrors <see cref="WorkItemPage"/> but carries
/// API-shaped <see cref="WorkItemListItemResponse"/>s — a slimmer
/// per-item shape than <see cref="WorkItemResponse"/> that omits the
/// per-item <c>Notes</c> and <c>AuditLog</c> collections (epr-4pf). A
/// 100-row page of an item with chatty assessor activity used to ship
/// the entire audit history and every note for every row even though
/// the list view never displays them; the slim shape keeps the wire
/// payload bounded by the envelope size.
///
/// Callers that need the full timeline must hit
/// <c>GET /work-items/{id}</c>, which still returns the unmodified
/// <see cref="WorkItemResponse"/>.
/// </summary>
public sealed record WorkItemListResponse(
    IReadOnlyList<WorkItemListItemResponse> Items,
    long TotalCount,
    int Page,
    int PageSize);

/// <summary>
/// Per-item shape returned by the list endpoint (epr-4pf). Mirrors
/// <see cref="WorkItemResponse"/> exactly except that the
/// <c>Notes</c> and <c>AuditLog</c> collections are omitted entirely
/// (the JSON properties do not exist on the wire — they are not
/// emitted-as-null) so list responses stay small. Every other field —
/// available actions, assignment snapshot, template version — is kept so
/// a list view can render rich rows without a per-row round-trip.
/// </summary>
public sealed record WorkItemListItemResponse(
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
    TimeSpan? SlaRemaining = null,
    WorkItemSlaState? SlaState = null,
    // RA-324: absolute SLA deadline (slaClock.StartedAt + TargetDuration) so the
    // Applications card can render "Due on: {date}". Null under the same
    // condition as SlaState/SlaRemaining — no SLA clock started yet. Additive +
    // nullable, so the list DTO stays backward-compatible.
    DateTime? SlaDueDate = null,
    // RA-410: the state this work item returns to when its current waypoint
    // discharges. Equal to StateId for any item not in a waypoint state.
    // Mirrors WorkItemResponse.OriginStateId so the single-item and list
    // shapes agree — see that field for why a client cannot derive it.
    // Additive + nullable, so the list DTO stays backward-compatible.
    string? OriginStateId = null);