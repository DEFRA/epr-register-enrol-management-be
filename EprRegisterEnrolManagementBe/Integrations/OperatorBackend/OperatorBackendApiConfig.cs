namespace EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

/// <summary>
/// Config for the RA-311/MBE-1 outbound push to the operator backend.
/// Mirrors the shape of the operator backend's own <c>CaseWorkingApiConfig</c>
/// (the adapter for the opposite direction) — a base URL, the client id this
/// service presents itself as, and an optional HMAC shared secret.
///
/// Bound from the <c>OperatorBackendApi</c> configuration section
/// (<c>OperatorBackendApi__Enabled</c> / <c>__Url</c> / <c>__ClientId</c> env
/// vars at deploy time), except <see cref="SharedSecret"/>, which — per
/// CDP's secrets naming convention — is sourced from the flat
/// <c>OPERATOR_BACKEND_SHARED_SECRET</c> env var instead. Validated on start by
/// <see cref="OperatorBackendApiConfigValidator"/>: <see cref="Url"/>,
/// <see cref="ClientId"/> and <see cref="SharedSecret"/> are only required
/// when <see cref="Enabled"/> is <c>true</c>.
/// </summary>
public sealed class OperatorBackendApiConfig
{
    /// <summary>
    /// Master switch for the outbound push (MBE-F5). Defaults to
    /// <c>false</c> so deploying this code is behaviour-neutral until it is
    /// explicitly turned on per environment, and doubles as the rollback
    /// lever — flip back to <c>false</c> to disable the push without a code
    /// deploy.
    /// </summary>
    public bool Enabled { get; set; }

    public string Url { get; set; } = string.Empty;

    public string ClientId { get; set; } = "epr-register-enrol-management-be";

    /// <summary>
    /// Sourced from the flat <c>OPERATOR_BACKEND_SHARED_SECRET</c> env var, not
    /// a nested <c>OperatorBackendApi__*</c> key — see Program.cs's binding.
    /// </summary>
    public string? SharedSecret { get; set; }

    /// <summary>
    /// Per-attempt timeout applied to each push attempt (including retries).
    /// Guards against the underlying <c>HttpClient</c>'s 100s default
    /// timeout, which — combined with the up-to-two retries — could
    /// otherwise stall the synchronous, request-path
    /// <see cref="EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReAccreditationQueryPushHook"/>
    /// (and therefore the caseworker's own query action request) for several
    /// minutes if the operator backend hangs rather than failing fast.
    /// Mirrors <c>Notify:RequestTimeoutSeconds</c>'s same-purpose guard on
    /// <c>GovukNotifyClient</c>'s outbound pipeline. Set to 0 to disable.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// epr-p86e / RA-410: retry budget for the <em>decision</em> status push
    /// only (<see cref="IOperatorBackendPushAdapter.PushDecisionStatusChangedAsync"/>),
    /// which — unlike every other best-effort push — is a hard pre-commit gate:
    /// the re-accreditation decision is abandoned with a 500 if it cannot be
    /// delivered, so it is worth trying harder than the fire-and-forget default
    /// (<c>MaxRetryAttempts = 2</c>). Five retries = six attempts total.
    /// </summary>
    public int DecisionPushMaxRetryAttempts { get; set; } = 5;

    /// <summary>
    /// epr-p86e / RA-410: per-attempt timeout for the decision status push.
    /// Deliberately shorter than <see cref="RequestTimeoutSeconds"/> because
    /// this push runs synchronously on the caseworker's own /decision request
    /// and, unlike the best-effort pushes, is retried up to
    /// <see cref="DecisionPushMaxRetryAttempts"/> times before failing — so the
    /// per-attempt bound has to stay small to keep the whole worst-case budget
    /// well under a minute. Worst case is
    /// <c>(DecisionPushMaxRetryAttempts + 1) × DecisionPushRequestTimeoutSeconds</c>
    /// of HTTP time plus the capped backoff between attempts; the case
    /// management frontend's /decision request timeout MUST exceed that total,
    /// or a client abort mid-retry re-creates the cancellation bug this change
    /// exists to fix. See <c>HttpOperatorBackendPushAdapter.BuildDecisionRetryPipeline</c>.
    /// </summary>
    public int DecisionPushRequestTimeoutSeconds { get; set; } = 3;
}