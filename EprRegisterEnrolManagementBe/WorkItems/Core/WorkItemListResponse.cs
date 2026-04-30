namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// API representation of a single page of work items returned by
/// <c>GET /work-items</c>. Mirrors <see cref="WorkItemPage"/> but carries
/// API-shaped <see cref="WorkItemResponse"/>s so callers do not see internal
/// types.
/// </summary>
public sealed record WorkItemListResponse(
    IReadOnlyList<WorkItemResponse> Items,
    long TotalCount,
    int Page,
    int PageSize);