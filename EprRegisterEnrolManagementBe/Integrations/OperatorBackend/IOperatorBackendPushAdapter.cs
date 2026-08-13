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
    /// reflects CM's lifecycle beyond just queries. <paramref name="correlationId"/>
    /// follows the same one-per-push, cross-repo-log-joining contract as
    /// <see cref="PushQueryRaisedAsync"/>.
    /// </summary>
    Task<OperatorBackendPushResult> PushStatusChangedAsync(
        Guid workItemId,
        Guid correlationId,
        string fromStateId,
        string toStateId,
        string toStateDisplayName,
        string actionId,
        string actionDisplayName,
        DateTime occurredAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// epr-p86e / RA-410: push a re-accreditation <em>decision</em> transition
    /// to the operator backend as a hard, pre-commit gate. Identical on the wire
    /// to <see cref="PushStatusChangedAsync"/> — same endpoint, body and signing
    /// — but backed by a larger retry budget
    /// (<see cref="OperatorBackendApiConfig.DecisionPushMaxRetryAttempts"/>),
    /// because the caller (<c>ReAccreditationLogDecisionService</c>) invokes it
    /// <em>before</em> persisting any state change and abandons the whole
    /// decision with a 500 on failure, rather than treating the push as
    /// fire-and-forget. Still never throws: a failure is reported as a
    /// non-success <see cref="OperatorBackendPushResult"/>, and
    /// <see cref="OperatorBackendPushResult.IsSkipped"/> (the push is disabled)
    /// is a pass the caller must not gate on.
    /// </summary>
    Task<OperatorBackendPushResult> PushDecisionStatusChangedAsync(
        Guid workItemId,
        Guid correlationId,
        string fromStateId,
        string toStateId,
        string toStateDisplayName,
        string actionId,
        string actionDisplayName,
        DateTime occurredAt,
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