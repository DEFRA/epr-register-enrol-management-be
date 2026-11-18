namespace EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

/// <summary>
/// RA-311/MBE-1: outbound push to the operator backend when a
/// re-accreditation query is raised, so the operator's own record reflects
/// the query note and queried sections without polling. The mirror-image
/// direction of the operator backend's own <c>HttpCaseWorkingApiAdapter</c>
/// (its calls into <c>POST /work-items</c> / <c>GET /work-items/{id}</c>
/// on this service).
///
/// Implementations must never throw — a push failure must not unwind the
/// already-persisted query transition. See
/// <see cref="EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReAccreditationQueryPushHook"/>.
/// </summary>
public interface IOperatorBackendPushAdapter
{
    /// <summary>
    /// <paramref name="correlationId"/> is generated once per push attempt by
    /// the caller (<c>ReAccreditationQueryPushHook</c>), sent to the operator
    /// backend as the <c>X-Correlation-Id</c> header, and included on every
    /// log line and audit entry associated with this push so the two sides'
    /// logs can be joined on a single value (RA-311/MBE-1 cross-repo
    /// contract).
    /// </summary>
    Task<OperatorBackendPushResult> PushQueryRaisedAsync(
        Guid workItemId,
        Guid correlationId,
        string queryNote,
        IReadOnlyList<string> sectionKeys,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// RA-368: push a work item's state transition to the operator backend so
    /// its own record of application progress (<c>ApplicationStatus</c>)
    /// reflects the Case Management service's lifecycle beyond just queries.
    /// <see cref="OperatorJourneyStatusChange.CorrelationId"/>
    /// follows the same one-per-push, cross-repo-log-joining contract as
    /// <see cref="PushQueryRaisedAsync"/>.
    ///
    /// Every status push has the same body and signing; it differs only in
    /// <paramref name="route"/> (which operator-journey record it updates) and
    /// <paramref name="pipeline"/>. <see cref="OperatorBackendPushPipeline.Decision"/>
    /// (epr-p86e / RA-410) is for a caller that pushes <em>before</em>
    /// persisting anything and abandons the whole decision with a 500 on
    /// failure (<c>StandardDecisionPush</c>), so it gets a larger retry budget
    /// (<see cref="OperatorBackendApiConfig.DecisionPushMaxRetryAttempts"/>).
    /// Still never throws: a failure is reported as a non-success
    /// <see cref="OperatorBackendPushResult"/>, and
    /// <see cref="OperatorBackendPushResult.IsSkipped"/> (the push is disabled)
    /// is a pass a gating caller must not refuse on.
    /// </summary>
    Task<OperatorBackendPushResult> PushStatusChangedAsync(
        OperatorBackendStatusRoute route,
        OperatorBackendPushPipeline pipeline,
        OperatorJourneyStatusChange change,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of a push attempt. Never throws its way out of the adapter.
/// <see cref="IsSkipped"/> is distinct from a failure — it means the push
/// was never attempted because it is deliberately disabled
/// (<c>OperatorBackendApi:Enabled=false</c>), so callers can record a
/// non-alerting <c>query-push-skipped</c> outcome instead of
/// <c>query-push-failed</c> (MBE-F5).
/// </summary>
public sealed record OperatorBackendPushResult(bool IsSuccess, bool IsSkipped, string? ErrorMessage)
{
    public static OperatorBackendPushResult Success() => new(true, false, null);

    public static OperatorBackendPushResult Skipped(string reason) => new(false, true, reason);

    public static OperatorBackendPushResult Failure(string errorMessage) => new(false, false, errorMessage);
}

/// <summary>
/// The operator-journey status route a status push goes to.
/// </summary>
public enum OperatorBackendStatusRoute
{
    /// <summary>
    /// RA-368: <c>POST api/v1/accreditation-applications/case-management/{workItemId}/status</c>,
    /// re-accreditation's route.
    /// </summary>
    AccreditationApplication,

    /// <summary>
    /// RA-495: <c>POST api/v1/accreditation-changes/case-management/{workItemId}/status</c>,
    /// an in-year change request's route.
    /// </summary>
    ChangeRequest,
}

/// <summary>
/// The retry budget a status push is sent with.
/// </summary>
public enum OperatorBackendPushPipeline
{
    /// <summary>The fire-and-forget post-commit push's small retry budget.</summary>
    BestEffort,

    /// <summary>
    /// epr-p86e / RA-410: the pre-commit decision gate's larger, bounded budget.
    /// </summary>
    Decision,
}

/// <summary>
/// RA-495: one status change as sent to the operator journey. The body every
/// operator-journey status route takes: from/to state, the to-state's display name, the
/// action and when it happened, plus the correlation id that joins the two
/// sides' logs.
/// </summary>
public sealed record OperatorJourneyStatusChange(
    Guid WorkItemId,
    Guid CorrelationId,
    string FromStateId,
    string ToStateId,
    string ToStateDisplayName,
    string ActionId,
    string ActionDisplayName,
    DateTime OccurredAt
);
