using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolManagementBe.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

/// <summary>
/// RA-311/MBE-1 real <see cref="IOperatorBackendPushAdapter"/>.
///
/// Uses the plain <c>"DefaultClient"</c> (explicitly <c>UseProxy = false</c>,
/// see <c>Program.cs::ConfigureHttpClients</c>) — this is CDP-service-to-CDP-service
/// traffic, the mirror image of the operator backend's own
/// <c>HttpCaseWorkingApiAdapter</c>, which also skips the egress proxy for its
/// calls into this service. Signs with
/// <see cref="ClientIdAuthenticationHandler.ComputeSignature"/> — the
/// same v3 canonical-payload HMAC this service's own inbound handler
/// verifies — reused here to *produce* a signature rather than verify one,
/// exactly as the operator backend's adapter does against this service
/// today.
///
/// Posts to OBE-2's real, confirmed contract:
/// <c>POST api/v1/accreditation-applications/case-management/{workItemId}/query</c>
/// with <c>workItemId</c> in the route and <c>{ queryNote, sectionKeys }</c>
/// as the body (RA-311 fix doc, MBE-F1/F2).
///
/// RA-368: also posts generic state-transition pushes to
/// <c>POST api/v1/accreditation-applications/case-management/{workItemId}/status</c>
/// via <see cref="PushStatusChangedAsync"/>, sharing the same signing,
/// retry and never-throws behaviour.
///
/// Retries transient failures (5xx / transport exceptions) up to twice with
/// jittered exponential backoff before giving up; never retries a 4xx, since
/// the most likely first-enablement failure is a systemic auth/validation
/// error that a retry would only amplify (MBE-F6).
///
/// epr-p86e / RA-410: <see cref="PushDecisionStatusChangedAsync"/> shares the
/// same wire contract and signing but uses its own, more aggressive pipeline
/// (<see cref="BuildDecisionRetryPipeline"/>) because it is a hard pre-commit
/// gate on the re-accreditation decision rather than a best-effort push — the
/// decision is abandoned with a 500 if it cannot be delivered.
/// </summary>
internal sealed class HttpOperatorBackendPushAdapter(
    IHttpClientFactory httpClientFactory,
    IOptions<OperatorBackendApiConfig> config,
    ILogger<HttpOperatorBackendPushAdapter> logger,
    ResiliencePipeline<HttpResponseMessage>? retryPipeline = null,
    ResiliencePipeline<HttpResponseMessage>? decisionRetryPipeline = null) : IOperatorBackendPushAdapter
{
    private const string QueryRelativePathTemplate =
        "/api/v1/accreditation-applications/case-management/{0}/query";

    private const string StatusRelativePathTemplate =
        "/api/v1/accreditation-applications/case-management/{0}/status";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // epr-p86e / RA-410: per-attempt backoff cap for the decision push, so the
    // jittered exponential delay between attempts can never exceed this — which
    // is what makes the decision push's total worst-case budget a firm number.
    private static readonly TimeSpan s_decisionMaxBackoff = TimeSpan.FromSeconds(2);

    private readonly OperatorBackendApiConfig _config = config.Value;
    private readonly ResiliencePipeline<HttpResponseMessage> _retryPipeline =
        retryPipeline ?? BuildRetryPipeline(logger, config.Value.RequestTimeoutSeconds);

    // epr-p86e / RA-410: the decision push is a hard pre-commit gate, so it gets
    // its own, more aggressive pipeline (more retries, a shorter per-attempt
    // timeout, and a capped backoff) — kept separate from the best-effort one
    // above so the non-decision pushes' behaviour is untouched.
    private readonly ResiliencePipeline<HttpResponseMessage> _decisionRetryPipeline =
        decisionRetryPipeline ?? BuildDecisionRetryPipeline(
            logger,
            config.Value.DecisionPushRequestTimeoutSeconds,
            config.Value.DecisionPushMaxRetryAttempts);

    public async Task<OperatorBackendPushResult> PushQueryRaisedAsync(
        Guid workItemId,
        Guid correlationId,
        string queryNote,
        IReadOnlyList<string> sectionKeys,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_config.Url))
        {
            return OperatorBackendPushResult.Failure("OperatorBackendApi:Url is not configured.");
        }

        var relativePath = string.Format(QueryRelativePathTemplate, Uri.EscapeDataString(workItemId.ToString()));
        var endpoint = $"{_config.Url.TrimEnd('/')}{relativePath}";

        // RA-311/MBE-1: request payload metadata + correlation id, logged
        // once up front regardless of how many retry attempts follow.
        // queryNote is regulator-supplied free text and is intentionally
        // never logged — not even as a structured parameter, since Serilog
        // still writes structured parameter values into the rendered
        // message/property set. Only its length is logged as a size signal.
        // This matches the rule the paired operator-backend change applies
        // to the same field (RA-311 security note).
        logger.LogInformation(
            "Pushing query-raised for work item {WorkItemId} to {Endpoint} (correlation {CorrelationId}); sections {SectionKeys}, note length {QueryNoteLength}",
            workItemId, endpoint, correlationId, sectionKeys, queryNote.Length);

        var body = new QueryRaisedPushRequest(queryNote, sectionKeys);
        return await ExecutePushAsync(
            workItemId, endpoint, correlationId, body, "query-raised", _retryPipeline, cancellationToken);
    }

    public Task<OperatorBackendPushResult> PushStatusChangedAsync(
        Guid workItemId,
        Guid correlationId,
        string fromStateId,
        string toStateId,
        string toStateDisplayName,
        string actionId,
        string actionDisplayName,
        DateTime occurredAt,
        CancellationToken cancellationToken = default) =>
        PushStatusInternalAsync(
            workItemId, correlationId, fromStateId, toStateId, toStateDisplayName, actionId,
            actionDisplayName, occurredAt, _retryPipeline, "status-changed", cancellationToken);

    public Task<OperatorBackendPushResult> PushDecisionStatusChangedAsync(
        Guid workItemId,
        Guid correlationId,
        string fromStateId,
        string toStateId,
        string toStateDisplayName,
        string actionId,
        string actionDisplayName,
        DateTime occurredAt,
        CancellationToken cancellationToken = default) =>
        PushStatusInternalAsync(
            workItemId, correlationId, fromStateId, toStateId, toStateDisplayName, actionId,
            actionDisplayName, occurredAt, _decisionRetryPipeline, "decision-status-changed", cancellationToken);

    /// <summary>
    /// Shared body for both status pushes. Identical on the wire — same
    /// endpoint, body and signing — differing only in the resilience
    /// <paramref name="pipeline"/> (best-effort vs the decision gate's larger
    /// budget) and the <paramref name="operationName"/> used in logs.
    /// </summary>
    private async Task<OperatorBackendPushResult> PushStatusInternalAsync(
        Guid workItemId,
        Guid correlationId,
        string fromStateId,
        string toStateId,
        string toStateDisplayName,
        string actionId,
        string actionDisplayName,
        DateTime occurredAt,
        ResiliencePipeline<HttpResponseMessage> pipeline,
        string operationName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_config.Url))
        {
            return OperatorBackendPushResult.Failure("OperatorBackendApi:Url is not configured.");
        }

        var relativePath = string.Format(StatusRelativePathTemplate, Uri.EscapeDataString(workItemId.ToString()));
        var endpoint = $"{_config.Url.TrimEnd('/')}{relativePath}";

        logger.LogInformation(
            "Pushing {Operation} for work item {WorkItemId} to {Endpoint} (correlation {CorrelationId}); {FromStateId} -> {ToStateId} via {ActionId}",
            operationName, workItemId, endpoint, correlationId, fromStateId, toStateId, actionId);

        var body = new StatusChangedPushRequest(
            fromStateId, toStateId, toStateDisplayName, actionId, actionDisplayName, occurredAt);
        return await ExecutePushAsync(workItemId, endpoint, correlationId, body, operationName, pipeline, cancellationToken);
    }

    /// <summary>
    /// Shared attempt/retry/response-handling shape for both push
    /// operations: rebuilds a fresh signed request per attempt, retries
    /// transient (5xx/transport) failures via <see cref="_retryPipeline"/>,
    /// and never throws its way out — a push failure must not unwind the
    /// already-persisted transition it reports on.
    /// </summary>
    private async Task<OperatorBackendPushResult> ExecutePushAsync<TBody>(
        Guid workItemId, string endpoint, Guid correlationId, TBody body, string operationName,
        ResiliencePipeline<HttpResponseMessage> pipeline,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await pipeline.ExecuteAsync(
                async ct =>
                {
                    // Rebuilt on every attempt (retry included): the request
                    // content can only be sent once, and a fresh
                    // timestamp/nonce per attempt is correct anyway — see
                    // A4 in the RA-311 fix doc (5-minute signature window).
                    using var request = BuildRequest(endpoint, correlationId, body);
                    var client = httpClientFactory.CreateClient("DefaultClient");
                    return await client.SendAsync(request, ct);
                },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError(
                    "Operator backend returned {Status} from {Endpoint} for work item {WorkItemId} (correlation {CorrelationId}): {Body}",
                    (int)response.StatusCode, endpoint, workItemId, correlationId, responseBody);
                return OperatorBackendPushResult.Failure(
                    $"Operator backend returned {(int)response.StatusCode} from {endpoint}.");
            }

            logger.LogInformation(
                "Operator backend {Operation} push succeeded for work item {WorkItemId} (correlation {CorrelationId}); status {Status}.",
                operationName, workItemId, correlationId, (int)response.StatusCode);
            return OperatorBackendPushResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Failed to push {Operation} for work item {WorkItemId} to {Endpoint} (correlation {CorrelationId})",
                operationName, workItemId, endpoint, correlationId);
            return OperatorBackendPushResult.Failure(ex.Message);
        }
    }

    /// <summary>
    /// 2 retries (3 attempts total) with jittered exponential backoff, each
    /// attempt bounded by <paramref name="requestTimeoutSeconds"/>. Retries
    /// transport exceptions, per-attempt timeouts, and 5xx responses only —
    /// never a 4xx, which on first enablement is most likely a systemic
    /// auth/contract problem (e.g. a shared-secret mismatch) that a retry
    /// would only triple the volume of, not fix.
    ///
    /// The per-attempt timeout guards against the underlying
    /// <c>HttpClient</c>'s 100s default (no timeout is configured on the
    /// "DefaultClient" registration) which, combined with up to two
    /// retries, could otherwise stall this adapter's caller — the
    /// synchronous, request-path <c>ReAccreditationQueryPushHook</c> — for
    /// several minutes if the operator backend hangs rather than
    /// responding with an error. Strategy order matters: retry is outer,
    /// timeout is inner, so the timeout applies to each attempt
    /// individually rather than to the whole retry sequence (mirrors
    /// <c>GovukNotifyClient</c>'s pipeline for the same reason).
    /// </summary>
    private static ResiliencePipeline<HttpResponseMessage> BuildRetryPipeline(
        ILogger logger, int requestTimeoutSeconds)
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(500),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .Handle<TimeoutRejectedException>()
                    .HandleResult(response => (int)response.StatusCode >= 500),
                OnRetry = args =>
                {
                    logger.LogWarning(
                        "Operator backend push attempt {Attempt} failed{StatusInfo}; retrying in {DelayMs}ms.",
                        args.AttemptNumber + 1,
                        args.Outcome.Result is { } result ? $" (HTTP {(int)result.StatusCode})" : string.Empty,
                        (long)args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                },
            });

        if (requestTimeoutSeconds > 0)
        {
            builder.AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(requestTimeoutSeconds),
                OnTimeout = args =>
                {
                    logger.LogWarning(
                        "Operator backend push attempt timed out after {TimeoutSeconds}s.",
                        args.Timeout.TotalSeconds);
                    return ValueTask.CompletedTask;
                },
            });
        }

        return builder.Build();
    }

    /// <summary>
    /// epr-p86e / RA-410: the decision push's own pipeline. Same 5xx/transport
    /// retry predicate and outer-retry/inner-timeout ordering as
    /// <see cref="BuildRetryPipeline"/>, but tuned for a hard, synchronous,
    /// request-path gate rather than a fire-and-forget best-effort push:
    /// <list type="bullet">
    ///   <item><paramref name="maxRetryAttempts"/> (default 5) instead of 2 —
    ///   worth trying harder because the decision is abandoned with a 500 if
    ///   this fails.</item>
    ///   <item>a shorter per-attempt timeout
    ///   (<paramref name="requestTimeoutSeconds"/>, default 3s).</item>
    ///   <item>a <see cref="RetryStrategyOptions{T}.MaxDelay"/> cap on the
    ///   jittered backoff so the whole worst-case budget is a firm, bounded
    ///   number the frontend can set its /decision timeout safely above.</item>
    /// </list>
    /// Worst case ≈ (maxRetryAttempts + 1) × requestTimeoutSeconds of HTTP time
    /// + maxRetryAttempts × <see cref="s_decisionMaxBackoff"/> of backoff. With
    /// the defaults: 6 × 3s + 5 × 2s = 28s. The case-management frontend's
    /// /decision request timeout MUST exceed that or a client abort mid-retry
    /// re-creates the cancellation bug (epr-p86e).
    /// </summary>
    private static ResiliencePipeline<HttpResponseMessage> BuildDecisionRetryPipeline(
        ILogger logger, int requestTimeoutSeconds, int maxRetryAttempts)
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = maxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(250),
                // Caps each jittered backoff delay so the total budget stays a
                // firm number (see the worst-case sum in the summary above).
                MaxDelay = s_decisionMaxBackoff,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .Handle<TimeoutRejectedException>()
                    .HandleResult(response => (int)response.StatusCode >= 500),
                OnRetry = args =>
                {
                    logger.LogWarning(
                        "Operator backend decision push attempt {Attempt} failed{StatusInfo}; retrying in {DelayMs}ms.",
                        args.AttemptNumber + 1,
                        args.Outcome.Result is { } result ? $" (HTTP {(int)result.StatusCode})" : string.Empty,
                        (long)args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                },
            });

        if (requestTimeoutSeconds > 0)
        {
            builder.AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(requestTimeoutSeconds),
                OnTimeout = args =>
                {
                    logger.LogWarning(
                        "Operator backend decision push attempt timed out after {TimeoutSeconds}s.",
                        args.Timeout.TotalSeconds);
                    return ValueTask.CompletedTask;
                },
            });
        }

        return builder.Build();
    }

    private HttpRequestMessage BuildRequest<TBody>(string endpoint, Guid correlationId, TBody body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };

        request.Headers.Add("x-cdp-client-id", _config.ClientId);
        // RA-311/MBE-1 cross-repo contract: one correlation id per push,
        // generated by the caller and forwarded as a header so both
        // services' logs for this push can be joined on the same value.
        request.Headers.Add("X-Correlation-Id", correlationId.ToString());

        if (!string.IsNullOrEmpty(_config.SharedSecret))
        {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var signature = ClientIdAuthenticationHandler.ComputeSignature(
                _config.SharedSecret, _config.ClientId, userId: null, userName: null, timestamp, nonce);

            request.Headers.Add("x-cdp-auth-signature", signature);
            request.Headers.Add("x-cdp-auth-timestamp", timestamp);
            request.Headers.Add("x-cdp-auth-nonce", nonce);
        }

        return request;
    }

    private sealed record QueryRaisedPushRequest(string QueryNote, IReadOnlyList<string> SectionKeys);

    private sealed record StatusChangedPushRequest(
        string FromStateId,
        string ToStateId,
        string ToStateDisplayName,
        string ActionId,
        string ActionDisplayName,
        DateTime OccurredAt);
}