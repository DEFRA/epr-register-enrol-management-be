using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

/// <summary>
/// RA-448 phase 2 real <see cref="IAccreditationNumberAdapter"/>.
///
/// Reuses <see cref="OperatorBackendApiConfig"/> (same base URL, client id
/// and shared secret as <see cref="HttpOperatorBackendPushAdapter"/> — this
/// is the same backend service, just a different route), the same
/// unproxied "DefaultClient", and the shared <see cref="OperatorBackendJson"/>
/// / <see cref="OperatorBackendSigning"/> helpers for the wire contract and
/// v3 HMAC signing both adapters use.
///
/// Posts to the backend's confirmed phase 1 contract:
/// <c>POST api/v1/accreditation-applications/{organisationId}/{applicationId}/accreditation-number</c>
/// with <c>{ Nation, OrgId, Year, Regenerate }</c> as the body, matching
/// <c>AccreditationApplicationEndpoints.GenerateOrUpdateRegulatoryNumberRequest</c>'s
/// exact shape. Reads <c>accreditationReference</c> back off the response
/// body (the backend returns the full, camelCase-serialised
/// AccreditationApplicationModel).
///
/// Checks <see cref="OperatorBackendApiConfig.Enabled"/>, not just
/// <see cref="OperatorBackendApiConfig.Url"/> — unlike the best-effort push
/// adapter's real-vs-no-op DI selection (<c>ConfigureOperatorBackendPush</c>),
/// this adapter is registered unconditionally (there is no safe no-op for a
/// call approval depends on), so it has to enforce the Enabled=false
/// behaviour-neutral invariant (MBE-F5) itself: an environment that has Url
/// pre-populated ahead of turning Enabled on (a normal rollout sequence)
/// must not have this adapter start firing real signed HTTP calls anyway.
///
/// Retries transient failures (5xx / transport exceptions) with a firm,
/// capped worst-case budget (<see cref="MaxRetryAttempts"/> attempts,
/// <see cref="PerAttemptTimeoutSeconds"/>s each, backoff capped at
/// <see cref="s_maxBackoff"/>) — deliberately NOT sourced from
/// <see cref="OperatorBackendApiConfig.RequestTimeoutSeconds"/>, the
/// shared/uncapped knob <see cref="HttpOperatorBackendPushAdapter"/>'s
/// best-effort pipeline uses: this call sits on a synchronous, user-facing
/// approval request, so its worst case must stay small and predictable
/// rather than drifting if that shared setting is ever tuned for the
/// unrelated best-effort push. Never retries a 4xx.
/// </summary>
internal sealed class HttpAccreditationNumberAdapter(
    IHttpClientFactory httpClientFactory,
    IOptions<OperatorBackendApiConfig> config,
    ILogger<HttpAccreditationNumberAdapter> logger,
    ResiliencePipeline<HttpResponseMessage>? retryPipeline = null
) : IAccreditationNumberAdapter
{
    private const string RelativePathTemplate =
        "/api/v1/accreditation-applications/{0}/{1}/accreditation-number";

    // Deliberately local to this adapter, not read from OperatorBackendApiConfig
    // — see the class doc comment for why this budget must stay firm and
    // decoupled from the best-effort push's shared timeout knob.
    private const int MaxRetryAttempts = 2;
    private const int PerAttemptTimeoutSeconds = 5;
    private static readonly TimeSpan s_maxBackoff = TimeSpan.FromSeconds(2);

    private readonly OperatorBackendApiConfig _config = config.Value;
    private readonly ResiliencePipeline<HttpResponseMessage> _retryPipeline =
        retryPipeline ?? BuildRetryPipeline(logger);

    public async Task<AccreditationNumberResult> GenerateOrUpdateAccreditationNumberAsync(
        AccreditationNumberRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (!_config.Enabled)
        {
            return AccreditationNumberResult.Failure("OperatorBackendApi:Enabled is false.");
        }

        if (string.IsNullOrWhiteSpace(_config.Url))
        {
            return AccreditationNumberResult.Failure("OperatorBackendApi:Url is not configured.");
        }

        var relativePath = string.Format(
            RelativePathTemplate,
            Uri.EscapeDataString(request.OrganisationId),
            Uri.EscapeDataString(request.ApplicationId)
        );
        var endpoint = $"{_config.Url.TrimEnd('/')}{relativePath}";

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Requesting accreditation number for organisation {OrganisationId} application "
                    + "{ApplicationId} from {Endpoint} (nation {Nation}, year {Year}, regenerate {Regenerate}, "
                    + "correlation {CorrelationId})",
                request.OrganisationId,
                request.ApplicationId,
                endpoint,
                request.Nation,
                request.Year,
                request.Regenerate,
                request.CorrelationId
            );
        }

        var body = new BackendRequestBody(
            request.Nation.ToString(),
            request.OrgId,
            request.Year,
            request.Regenerate
        );

        try
        {
            var response = await _retryPipeline.ExecuteAsync(
                async ct =>
                {
                    // Rebuilt on every attempt (retry included): request content
                    // can only be sent once, and a fresh timestamp/nonce per
                    // attempt is correct anyway (signature window).
                    using var httpRequest = BuildRequest(endpoint, request.CorrelationId, body);
                    var client = httpClientFactory.CreateClient("DefaultClient");
                    return await client.SendAsync(httpRequest, ct);
                },
                cancellationToken
            );

            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError(
                    "Backend returned {Status} from {Endpoint} for organisation {OrganisationId} "
                        + "application {ApplicationId} (correlation {CorrelationId}): {Body}",
                    (int)response.StatusCode,
                    endpoint,
                    request.OrganisationId,
                    request.ApplicationId,
                    request.CorrelationId,
                    responseBody
                );
                return AccreditationNumberResult.Failure(
                    $"Backend returned {(int)response.StatusCode} from {endpoint}."
                );
            }

            var payload =
                await response.Content.ReadFromJsonAsync<AccreditationApplicationResponse>(
                    OperatorBackendJson.Options,
                    cancellationToken
                );
            if (string.IsNullOrWhiteSpace(payload?.AccreditationReference))
            {
                logger.LogError(
                    "Backend returned a success status from {Endpoint} for organisation "
                        + "{OrganisationId} application {ApplicationId} (correlation {CorrelationId}) but the "
                        + "response carried no accreditationReference.",
                    endpoint,
                    request.OrganisationId,
                    request.ApplicationId,
                    request.CorrelationId
                );
                return AccreditationNumberResult.Failure(
                    "Backend response did not include an accreditation reference."
                );
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Accreditation number resolved for organisation {OrganisationId} application "
                        + "{ApplicationId} (correlation {CorrelationId}).",
                    request.OrganisationId,
                    request.ApplicationId,
                    request.CorrelationId
                );
            }
            return AccreditationNumberResult.Success(payload.AccreditationReference);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's own token was cancelled (request aborted, upstream
            // timeout) — this is not a backend failure, so it must propagate
            // as a cancellation rather than being reported as an
            // AccreditationNumberResult.Failure the caller would otherwise
            // treat as a definite, retriable business outcome.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to resolve accreditation number for organisation {OrganisationId} application "
                    + "{ApplicationId} from {Endpoint} (correlation {CorrelationId})",
                request.OrganisationId,
                request.ApplicationId,
                endpoint,
                request.CorrelationId
            );
            return AccreditationNumberResult.Failure(ex.Message);
        }
    }

    private HttpRequestMessage BuildRequest<TBody>(string endpoint, Guid correlationId, TBody body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body, options: OperatorBackendJson.Options),
        };

        request.Headers.Add("X-Correlation-Id", correlationId.ToString());
        OperatorBackendSigning.AddHeaders(request, _config);

        return request;
    }

    /// <summary>
    /// <see cref="MaxRetryAttempts"/> retries, jittered exponential backoff
    /// capped at <see cref="s_maxBackoff"/>, each attempt bounded by
    /// <see cref="PerAttemptTimeoutSeconds"/>. Retries transport exceptions,
    /// per-attempt timeouts, and 5xx responses only — never a 4xx, which is
    /// most likely a systemic auth/contract problem a retry would not fix.
    /// Firm worst case: (MaxRetryAttempts + 1) * PerAttemptTimeoutSeconds +
    /// MaxRetryAttempts * s_maxBackoff ≈ 3*5s + 2*2s = 19s — the case
    /// management frontend's timeout for this call must exceed that.
    /// </summary>
    private static ResiliencePipeline<HttpResponseMessage> BuildRetryPipeline(ILogger logger) =>
        OperatorBackendRetryPipeline.Build(
            logger,
            "Accreditation number request",
            MaxRetryAttempts,
            PerAttemptTimeoutSeconds,
            s_maxBackoff
        );

    private sealed record BackendRequestBody(
        string? Nation,
        int? OrgId,
        int? Year,
        bool Regenerate
    );

    private sealed record AccreditationApplicationResponse(string? AccreditationReference);
}
