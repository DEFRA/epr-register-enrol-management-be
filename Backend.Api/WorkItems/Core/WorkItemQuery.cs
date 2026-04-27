namespace Backend.Api.WorkItems.Core;

/// <summary>
/// Filter, search and pagination parameters accepted by
/// <see cref="IWorkItemPersistence.QueryAsync"/>.
/// </summary>
/// <param name="TypeIds">
/// Restrict to items whose <see cref="WorkItem.TypeId"/> is in this set.
/// Empty/null means "any type".
/// </param>
/// <param name="StateIds">
/// Restrict to items whose <see cref="WorkItem.StateId"/> is in this set.
/// Empty/null means "any state".
/// </param>
/// <param name="Search">
/// Free-text needle. Matched case-insensitively against <see cref="WorkItem.Id"/>
/// (full or prefix) and <see cref="WorkItem.SubmittedBy"/>. Whitespace-only
/// values are ignored.
/// </param>
/// <param name="Page">1-based page number. Coerced to a minimum of 1.</param>
/// <param name="PageSize">Page size. Coerced into [<see cref="MinPageSize"/>, <see cref="MaxPageSize"/>].</param>
public sealed record WorkItemQuery(
    IReadOnlyCollection<string>? TypeIds = null,
    IReadOnlyCollection<string>? StateIds = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 20)
{
    public const int DefaultPageSize = 20;
    public const int MinPageSize = 1;
    public const int MaxPageSize = 100;

    /// <summary>The 1-based page number, clamped to a minimum of 1.</summary>
    public int NormalisedPage => Page < 1 ? 1 : Page;

    /// <summary>The page size clamped into [<see cref="MinPageSize"/>, <see cref="MaxPageSize"/>].</summary>
    public int NormalisedPageSize =>
        PageSize < MinPageSize ? MinPageSize :
        PageSize > MaxPageSize ? MaxPageSize : PageSize;

    /// <summary>Trimmed search needle, or <c>null</c> if blank/whitespace.</summary>
    public string? NormalisedSearch =>
        string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
}

/// <summary>
/// One page of work items returned from <see cref="IWorkItemPersistence.QueryAsync"/>.
/// </summary>
public sealed record WorkItemPage(
    IReadOnlyList<WorkItem> Items,
    long TotalCount,
    int Page,
    int PageSize);
