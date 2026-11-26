using System.Globalization;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-770: <c>GET /work-items/{id}/related-work-items?page=</c>, the case's
/// Related change requests tab. Every work item created from the same
/// submission as the one asked about (matched on
/// <c>payload.submissionReference</c>), a page of <see cref="PageSize"/> at a
/// time, with the one asked about first on page 1 and the rest in reference
/// order. A work item with no submission reference lists only itself.
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
    /// One page of the work items submitted with <paramref name="id"/>. The
    /// page is read as text so any value answers a page: one that is not a
    /// whole number, or is below 1, is page 1, and one past the end, however
    /// many digits it has, is the last page.
    /// </summary>
    public static async Task<Results<Ok<RelatedWorkItemsResponse>, NotFound>> GetRelatedWorkItems(
        [FromRoute] Guid id,
        [FromQuery] string? page,
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
        var currentPage = ResolvePage(page, lastPage);
        var items = ordered
            .Skip((currentPage - 1) * PageSize)
            .Take(PageSize)
            .Select(item => RelatedWorkItemResponse.From(item, strategies.Find(item.TypeId)))
            .ToList();

        return TypedResults.Ok(new RelatedWorkItemsResponse(ordered.Count, currentPage, PageSize, items));
    }

    /// <summary>The one asked about, then the rest of its submission in the order read.</summary>
    private static List<WorkItem> ThisOneFirst(WorkItem workItem, IReadOnlyList<WorkItem> submission) =>
        [workItem, .. submission.Where(item => item.Id != workItem.Id)];

    private static int ResolvePage(string? page, int lastPage)
    {
        if (string.IsNullOrEmpty(page) || !page.All(char.IsAsciiDigit))
        {
            return 1;
        }

        // All digits: too many to parse is past the end, so the last page.
        return long.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out var requested)
            ? (int)Math.Clamp(requested, 1, lastPage)
            : lastPage;
    }
}

/// <summary>RA-770: one page of the work items from the same submission.</summary>
internal sealed record RelatedWorkItemsResponse(
    int TotalCount,
    int Page,
    int PageSize,
    IReadOnlyList<RelatedWorkItemResponse> Items
);
