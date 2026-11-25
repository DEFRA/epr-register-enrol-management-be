using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-770: <c>GET /work-items/{id}/related-work-items?page=</c>, the case's
/// Related change requests tab. Every work item created from the same submission as
/// the one asked about (matched on <c>payload.submissionReference</c>), a page
/// of <see cref="PageSize"/> at a time, with the one asked about first on page 1
/// and the rest in reference order. A work item with no submission reference
/// lists only itself.
///
/// A submission holds one work item per site, so it is small: the whole
/// submission is read with the indexed
/// <see cref="IWorkItemPersistence.FindBySubmissionReferenceAsync"/> and paged
/// here, which keeps the "this one first" ordering a plain list operation.
/// </summary>
internal static class RelatedWorkItemsEndpoints
{
    public const int PageSize = 10;

    public static IEndpointRouteBuilder MapRelatedWorkItemsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/work-items")
            .WithTags("WorkItems")
            .MapGet("/{id:guid}/related-work-items", GetRelatedWorkItems)
            .WithName("GetRelatedWorkItems")
            .RequireAuthorization();

        return app;
    }

    /// <summary>
    /// One page of the work items submitted with <paramref name="id"/>. A page
    /// below 1 is page 1, and a page past the end is the last page.
    /// </summary>
    public static async Task<Results<Ok<RelatedWorkItemsResponse>, NotFound>> GetRelatedWorkItems(
        [FromRoute] Guid id,
        [FromQuery] int? page,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] StandardLifecycleStrategies strategies,
        CancellationToken cancellationToken
    )
    {
        var workItem = await persistence.GetByIdAsync(id, cancellationToken);
        if (workItem is null)
        {
            return TypedResults.NotFound();
        }

        var ordered = SubmissionPayload.TryReadSubmissionReference(workItem.Payload, out var reference)
            ? ThisOneFirst(workItem, await persistence.FindBySubmissionReferenceAsync(reference, cancellationToken))
            : [workItem];

        var lastPage = Math.Max(1, (ordered.Count + PageSize - 1) / PageSize);
        var currentPage = Math.Clamp(page ?? 1, 1, lastPage);
        var items = ordered
            .Skip((currentPage - 1) * PageSize)
            .Take(PageSize)
            .Select(item => RelatedWorkItemResponse.From(item, strategies.Find(item.TypeId)))
            .ToList();

        return TypedResults.Ok(new RelatedWorkItemsResponse(id, ordered.Count, currentPage, PageSize, items));
    }

    /// <summary>
    /// The one asked about, then the rest in the order read (reference order).
    /// It is put first even if the read missed it (it was written moments
    /// ago, say), so the count and page 1 always include it.
    /// </summary>
    private static List<WorkItem> ThisOneFirst(WorkItem workItem, IReadOnlyList<WorkItem> submission) =>
        [workItem, .. submission.Where(item => item.Id != workItem.Id)];
}

/// <summary>RA-770: one page of the work items from the same submission.</summary>
internal sealed record RelatedWorkItemsResponse(
    Guid WorkItemId,
    int TotalCount,
    int Page,
    int PageSize,
    IReadOnlyList<RelatedWorkItemResponse> Items
);

/// <summary>
/// RA-495: a work item from the same submission, as the Duly make page's
/// related change requests and (RA-770) the Related change requests tab show
/// it. The site fields are null for a work item with no <c>payload.site</c>;
/// the charge is null when its type has not opted in to the Standard lifecycle
/// or the work item carries none.
/// </summary>
internal sealed record RelatedWorkItemResponse(
    Guid Id,
    string TypeId,
    string StateId,
    string? ApplicationReference,
    string? SiteName,
    string? OrsId,
    int? ChargePence
)
{
    public static RelatedWorkItemResponse From(WorkItem workItem, IStandardLifecycleStrategy? strategy)
    {
        var payload = workItem.Payload;
        var site = SubmissionPayload.ReadSite(payload);
        return new RelatedWorkItemResponse(
            workItem.Id,
            workItem.TypeId,
            workItem.StateId,
            StringOrNull(payload, "applicationReference"),
            StringOrNull(site, "siteName"),
            StringOrNull(site, "orsId"),
            strategy?.ReadChargePence(workItem)
        );
    }

    private static string? StringOrNull(BsonDocument? document, string field) =>
        document is not null && document.TryGetValue(field, out var value) && value.IsString ? value.AsString : null;
}
