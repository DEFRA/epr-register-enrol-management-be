namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-698: the Standard workflow's query, resume and continue-review action
/// ids, resolved server-side so a caller never names one. Generalised from
/// re-accreditation's query and resume services for a type that follows the
/// Standard workflow but takes a reason-only query (add-ORS).
///
/// Every non-terminal pre-decision state has exactly one way out to
/// <c>queried</c>; <c>queried</c> has none, so an item cannot be queried
/// twice. The resume action that undoes a query is the one named after the
/// query action recorded on the item's own audit log.
/// </summary>
internal static class StandardQueryActions
{
    /// <summary>The state a query moves an item into.</summary>
    public const string QueriedStateId = "queried";

    private static readonly Dictionary<string, string> s_queryActionByState = new(StringComparer.OrdinalIgnoreCase)
    {
        ["submitted"] = "query-during-duly-making",
        ["duly-made"] = "query-during-duly-made",
        ["assessment-in-progress"] = "query-during-assessment",
        // A started case ("Duly made") is queried as one under assessment, so
        // its response and Continue review follow the assessment route.
        ["assessment-started"] = "query-during-assessment",
        ["awaiting-decision"] = "query-during-decision",
    };

    private static readonly Dictionary<string, string> s_resumeActionByQueryAction = new(StringComparer.OrdinalIgnoreCase)
    {
        ["query-during-duly-making"] = "resume-during-duly-making",
        ["query-during-duly-made"] = "resume-during-duly-made",
        ["query-during-assessment"] = "resume-during-assessment",
        ["query-during-decision"] = "resume-during-decision",
    };

    /// <summary>
    /// The states a resume lands in (<c>resume-during-duly-made</c> lands
    /// straight in assessment). A work item already in one of them has
    /// already been resumed, so a repeated resume is an idempotent replay.
    /// </summary>
    public static readonly IReadOnlySet<string> ResumeTargetStates = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "updated",
        "assessment-in-progress",
    };

    /// <summary>The states a continue-review lands in.</summary>
    public static readonly IReadOnlySet<string> ContinueReviewTargetStates = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "submitted",
        "duly-made",
        "assessment-in-progress",
        "awaiting-decision",
    };

    /// <summary>The <c>query-during-*</c> action for a state, or null when the state cannot be queried.</summary>
    public static string? ResolveQueryActionId(string? stateId) =>
        stateId is not null && s_queryActionByState.TryGetValue(stateId, out var actionId) ? actionId : null;

    /// <summary>The <c>resume-during-*</c> action that undoes <paramref name="queryActionId"/>, or null.</summary>
    public static string? ResolveResumeActionId(string? queryActionId) =>
        queryActionId is not null && s_resumeActionByQueryAction.TryGetValue(queryActionId, out var actionId)
            ? actionId
            : null;
}
