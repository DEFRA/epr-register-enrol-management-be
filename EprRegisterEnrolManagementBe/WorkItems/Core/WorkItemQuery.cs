namespace EprRegisterEnrolManagementBe.WorkItems.Core;

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
/// <param name="AssigneeId">
/// Restrict to items assigned to this user id. Empty/null means "any
/// assignee". Mutually combinable with <paramref name="UnassignedOnly"/>:
/// supplying both narrows to the union (assigned to id OR unassigned),
/// which is the natural shape for a "show me my work and anything still up
/// for grabs" view.
/// </param>
/// <param name="UnassignedOnly">
/// When <c>true</c>, restricts to items that have no assignee. Combined with
/// <paramref name="AssigneeId"/> as described above.
/// </param>
/// <param name="Page">1-based page number. Coerced to a minimum of 1.</param>
/// <param name="PageSize">Page size. Coerced into [<see cref="MinPageSize"/>, <see cref="MaxPageSize"/>].</param>
/// <param name="SubmittedBy">
/// Tenancy isolation filter. **Server-set only.** This value is populated
/// by <c>WorkItemEndpoints.GetAll</c> from the authenticated caller's
/// <c>cognito:client_id</c> (or NameIdentifier) claim for non-case-worker
/// callers; case-worker callers leave it null to see every tenant.
/// <para>
/// This property is intentionally NOT bound from the query string by
/// <see cref="WorkItemQueryBinding"/>. A future contributor must not
/// "helpfully" wire it up: doing so would let any caller pass
/// <c>?submittedBy=other-tenant</c> and read another tenant's items,
/// breaking the isolation enforced by the endpoint (and the fail-closed
/// short-circuit added in epr-z0k for callers with no identifiable
/// submitter id). The binding actively discards the parameter — see
/// <see cref="WorkItemQueryBinding.FromQueryString"/>.
/// </para>
/// </param>
/// <param name="Nations">
/// Restrict to items whose <c>payload.nation</c> is in this set.
/// Empty/null means "any nation". Values are the string names of the
/// <see cref="EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models.Nation"/>
/// enum members (e.g. <c>England</c>, <c>NorthernIreland</c>).
/// </param>
public sealed record WorkItemQuery(
    IReadOnlyCollection<string>? TypeIds = null,
    IReadOnlyCollection<string>? StateIds = null,
    string? Search = null,
    string? AssigneeId = null,
    bool UnassignedOnly = false,
    int Page = 1,
    int PageSize = 20,
    string? SubmittedBy = null,
    IReadOnlyCollection<string>? Nations = null)
{
    public const int DefaultPageSize = 20;
    public const int MinPageSize = 1;
    public const int MaxPageSize = 100;
    /// <summary>
    /// Maximum page number. Together with <see cref="MaxPageSize"/> this caps
    /// the total skip cost (Mongo "skip" is O(skip)) and prevents an
    /// attacker from issuing requests like <c>?page=999999999</c> to force
    /// the database into pathological scans (a cheap DoS vector).
    /// </summary>
    public const int MaxPage = 1000;

    /// <summary>The 1-based page number, clamped to [1, <see cref="MaxPage"/>].</summary>
    public int NormalisedPage => Page < 1 ? 1 : Page > MaxPage ? MaxPage : Page;

    /// <summary>
    /// True when <see cref="Page"/> exceeds <see cref="MaxPage"/>. Endpoints
    /// should reject the request with 400 rather than silently clamping so
    /// the client cannot accidentally page off the end of the data.
    /// </summary>
    public bool ExceedsPageCap => Page > MaxPage;

    /// <summary>The page size clamped into [<see cref="MinPageSize"/>, <see cref="MaxPageSize"/>].</summary>
    public int NormalisedPageSize =>
        PageSize < MinPageSize ? MinPageSize :
        PageSize > MaxPageSize ? MaxPageSize : PageSize;

    /// <summary>Trimmed search needle, or <c>null</c> if blank/whitespace.</summary>
    public string? NormalisedSearch =>
        string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();

    /// <summary>Trimmed assignee id, or <c>null</c> if blank/whitespace.</summary>
    public string? NormalisedAssigneeId =>
        string.IsNullOrWhiteSpace(AssigneeId) ? null : AssigneeId.Trim();

    /// <summary>Trimmed submitted-by, or <c>null</c> if blank/whitespace.</summary>
    public string? NormalisedSubmittedBy =>
        string.IsNullOrWhiteSpace(SubmittedBy) ? null : SubmittedBy.Trim();
}

/// <summary>
/// One page of work items returned from <see cref="IWorkItemPersistence.QueryAsync"/>.
/// </summary>
public sealed record WorkItemPage(
    IReadOnlyList<WorkItem> Items,
    long TotalCount,
    int Page,
    int PageSize);