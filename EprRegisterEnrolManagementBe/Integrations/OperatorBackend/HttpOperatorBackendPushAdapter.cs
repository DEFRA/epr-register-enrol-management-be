using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolManagementBe.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

/// <summary>
/// RA-311/MBE-1 real <see cref="IOperatorBackendPushAdapter"/>.
///
/// Uses the plain <c>"DefaultClient"</c> (no <c>ProxyHttpMessageHandler</c>)
/// — this is CDP-service-to-CDP-service traffic, the mirror image of the
/// operator backend's own <c>HttpCaseWorkingApiAdapter</c>, which also skips
/// the egress proxy for its calls into this service. Signs with
/// <see cref="CognitoClientIdAuthenticationHandler.ComputeSignature"/> — the
/// same v2 canonical-payload HMAC this service's own inbound handler
/// verifies — reused here to *produce* a signature rather than verify one,
/// exactly as the operator backend's adapter does against this service
/// today.
///
/// Posts to OBE-2's real, confirmed contract:
/// <c>POST api/v1/accreditation-applications/case-management/{workItemId}/query</c>
/// with <c>workItemId</c> in the route and <c>{ queryNote, sectionKeys }</c>
/// as the body (RA-311 fix doc, MBE-F1/F2).
///
/// Retries transient failures (5xx / transport exceptions) up to twice with
/// jittered exponential backoff before giving up; never retries a 4xx, since
/// the most likely first-enablement failure is a systemic auth/validation
/// error that a retry would only amplify (MBE-F6).
/// </summary>
internal sealed class HttpOperatorBackendPushAdapter(
    IHttpClientFactory httpClientFactory,
    IOptions<OperatorBackendApiConfig> config,
    ILogger<HttpOperatorBackendPushAdapter> logger,
    ResiliencePipeline<HttpResponseMessage>? retryPipeline = null) : IOperatorBackendPushAdapter
{
    private const string RelativePathTemplate =
        "/api/v1/accreditation-applications/case-management/{0}/query";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly OperatorBackendApiConfig _config = config.Value;
    private readonly ResiliencePipeline<HttpResponseMessage> _retryPipeline =
        retryPipeline ?? BuildRetryPipeline(logger);

    public async Task<OperatorBackendPushResult> PushQueryRaisedAsync(
        Guid workItemId,
        string queryNote,
        IReadOnlyList<string> sectionKeys,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_config.Url))
        {
            return OperatorBackendPushResult.Failure("OperatorBackendApi:Url is not configured.");
        }

        var relativePath = string.Format(RelativePathTemplate, Uri.EscapeDataString(workItemId.ToString()));
        var endpoint = $"{_config.Url.TrimEnd('/')}{relativePath}";

        try
        {
            var response = await _retryPipeline.ExecuteAsync(
                async ct =>
                {
                    // Rebuilt on every attempt (retry included): the request
                    // content can only be sent once, and a fresh
                    // timestamp/nonce per attempt is correct anyway — see
                    // A4 in the RA-311 fix doc (5-minute signature window).
                    using var request = BuildRequest(endpoint, queryNote, sectionKeys);
                    var client = httpClientFactory.CreateClient("DefaultClient");
                    return await client.SendAsync(request, ct);
                },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "Operator backend returned {Status} from {Endpoint} for work item {WorkItemId}: {Body}",
                    (int)response.StatusCode, endpoint, workItemId, body);
                return OperatorBackendPushResult.Failure(
                    $"Operator backend returned {(int)response.StatusCode} from {endpoint}.");
            }

            return OperatorBackendPushResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Failed to push query-raised for work item {WorkItemId} to {Endpoint}", workItemId, endpoint);
            return OperatorBackendPushResult.Failure(ex.Message);
        }
    }

    /// <summary>
    /// 2 retries (3 attempts total) with jittered exponential backoff.
    /// Retries transport exceptions and 5xx responses only — never a 4xx,
    /// which on first enablement is most likely a systemic auth/contract
    /// problem (e.g. a shared-secret mismatch) that a retry would only
    /// triple the volume of, not fix.
    /// </summary>
    private static ResiliencePipeline<HttpResponseMessage> BuildRetryPipeline(ILogger logger) =>
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(500),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
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
            })
            .Build();

    private HttpRequestMessage BuildRequest(string endpoint, string queryNote, IReadOnlyList<string> sectionKeys)
    {
        var body = new QueryRaisedPushRequest(queryNote, sectionKeys);
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };

        request.Headers.Add("x-cdp-cognito-client-id", _config.ClientId);

        if (!string.IsNullOrEmpty(_config.SharedSecret))
        {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var signature = CognitoClientIdAuthenticationHandler.ComputeSignature(
                _config.SharedSecret, _config.ClientId, userId: null, userName: null, userRoles: null, timestamp, nonce);

            request.Headers.Add("x-cdp-auth-signature", signature);
            request.Headers.Add("x-cdp-auth-timestamp", timestamp);
            request.Headers.Add("x-cdp-auth-nonce", nonce);
        }

        return request;
    }

    private sealed record QueryRaisedPushRequest(string QueryNote, IReadOnlyList<string> SectionKeys);
}