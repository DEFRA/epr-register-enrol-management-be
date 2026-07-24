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
    Task<OperatorBackendPushResult> PushQueryRaisedAsync(
        Guid workItemId,
        string queryNote,
        IReadOnlyList<string> sectionKeys,
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