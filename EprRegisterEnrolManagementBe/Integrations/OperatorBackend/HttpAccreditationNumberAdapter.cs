using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
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
/// is the same backend service, just a different route) and the same
/// unproxied "DefaultClient" / v3 HMAC signing scheme.
///
/// Posts to the backend's confirmed phase 1 contract:
/// <c>POST api/v1/accreditation-applications/{organisationId}/{applicationId}/accreditation-number</c>
/// with <c>{ Nation, OrgId, Year, Regenerate }</c> as the body, matching
/// <c>AccreditationApplicationEndpoints.GenerateOrUpdateRegulatoryNumberRequest</c>'s
/// exact shape. Reads <c>accreditationReference</c> back off the response
/// body (the backend returns the full, camelCase-serialised
/// AccreditationApplicationModel).
///
/// Retries transient failures (5xx / transport exceptions) up to twice with
/// jittered exponential backoff, same shape as
/// <see cref="HttpOperatorBackendPushAdapter"/>'s best-effort pipeline —
/// never retries a 4xx, since that is most likely a systemic auth/contract
/// problem a retry would not fix.
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly OperatorBackendApiConfig _config = config.Value;
    private readonly ResiliencePipeline<HttpResponseMessage> _retryPipeline =
        retryPipeline ?? BuildRetryPipeline(logger, config.Value.RequestTimeoutSeconds);

    public async Task<AccreditationNumberResult> GenerateOrUpdateAccreditationNumberAsync(
        string organisationId,
        string applicationId,
        Nation nation,
        int orgId,
        int year,
        bool regenerate,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(_config.Url))
        {
            return AccreditationNumberResult.Failure("OperatorBackendApi:Url is not configured.");
        }

        var relativePath = string.Format(
            RelativePathTemplate,
            Uri.EscapeDataString(organisationId),
            Uri.EscapeDataString(applicationId)
        );
        var endpoint = $"{_config.Url.TrimEnd('/')}{relativePath}";

        logger.LogInformation(
            "Requesting accreditation number for organisation {OrganisationId} application "
                + "{ApplicationId} from {Endpoint} (nation {Nation}, year {Year}, regenerate {Regenerate})",
            organisationId,
            applicationId,
            endpoint,
            nation,
            year,
            regenerate
        );

        var body = new GenerateOrUpdateRegulatoryNumberRequest(
            nation.ToString(),
            orgId,
            year,
            regenerate
        );

        try
        {
            var response = await _retryPipeline.ExecuteAsync(
                async ct =>
                {
                    // Rebuilt on every attempt (retry included): request content
                    // can only be sent once, and a fresh timestamp/nonce per
                    // attempt is correct anyway (signature window).
                    using var request = BuildRequest(endpoint, body);
                    var client = httpClientFactory.CreateClient("DefaultClient");
                    return await client.SendAsync(request, ct);
                },
                cancellationToken
            );

            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError(
                    "Backend returned {Status} from {Endpoint} for organisation {OrganisationId} "
                        + "application {ApplicationId}: {Body}",
                    (int)response.StatusCode,
                    endpoint,
                    organisationId,
                    applicationId,
                    responseBody
                );
                return AccreditationNumberResult.Failure(
                    $"Backend returned {(int)response.StatusCode} from {endpoint}."
                );
            }

            var payload =
                await response.Content.ReadFromJsonAsync<AccreditationApplicationResponse>(
                    JsonOptions,
                    cancellationToken
                );
            if (string.IsNullOrWhiteSpace(payload?.AccreditationReference))
            {
                logger.LogError(
                    "Backend returned a success status from {Endpoint} for organisation "
                        + "{OrganisationId} application {ApplicationId} but the response carried no "
                        + "accreditationReference.",
                    endpoint,
                    organisationId,
                    applicationId
                );
                return AccreditationNumberResult.Failure(
                    "Backend response did not include an accreditation reference."
                );
            }

            logger.LogInformation(
                "Accreditation number resolved for organisation {OrganisationId} application {ApplicationId}.",
                organisationId,
                applicationId
            );
            return AccreditationNumberResult.Success(payload.AccreditationReference);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to resolve accreditation number for organisation {OrganisationId} application "
                    + "{ApplicationId} from {Endpoint}",
                organisationId,
                applicationId,
                endpoint
            );
            return AccreditationNumberResult.Failure(ex.Message);
        }
    }

    private HttpRequestMessage BuildRequest<TBody>(string endpoint, TBody body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };

        request.Headers.Add("x-cdp-client-id", _config.ClientId);

        if (!string.IsNullOrEmpty(_config.SharedSecret))
        {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var signature = ClientIdAuthenticationHandler.ComputeSignature(
                _config.SharedSecret,
                _config.ClientId,
                userId: null,
                userName: null,
                timestamp,
                nonce
            );

            request.Headers.Add("x-cdp-auth-signature", signature);
            request.Headers.Add("x-cdp-auth-timestamp", timestamp);
            request.Headers.Add("x-cdp-auth-nonce", nonce);
        }

        return request;
    }

    /// <summary>
    /// Same shape as <see cref="HttpOperatorBackendPushAdapter"/>'s
    /// best-effort pipeline: 2 retries, jittered exponential backoff, each
    /// attempt bounded by <paramref name="requestTimeoutSeconds"/>. Retries
    /// transport exceptions, per-attempt timeouts, and 5xx responses only —
    /// never a 4xx.
    /// </summary>
    private static ResiliencePipeline<HttpResponseMessage> BuildRetryPipeline(
        ILogger logger,
        int requestTimeoutSeconds
    )
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>().AddRetry(
            new RetryStrategyOptions<HttpResponseMessage>
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
                        "Accreditation number request attempt {Attempt} failed{StatusInfo}; retrying in {DelayMs}ms.",
                        args.AttemptNumber + 1,
                        args.Outcome.Result is { } result
                            ? $" (HTTP {(int)result.StatusCode})"
                            : string.Empty,
                        (long)args.RetryDelay.TotalMilliseconds
                    );
                    return ValueTask.CompletedTask;
                },
            }
        );

        if (requestTimeoutSeconds > 0)
        {
            builder.AddTimeout(
                new TimeoutStrategyOptions
                {
                    Timeout = TimeSpan.FromSeconds(requestTimeoutSeconds),
                    OnTimeout = args =>
                    {
                        logger.LogWarning(
                            "Accreditation number request attempt timed out after {TimeoutSeconds}s.",
                            args.Timeout.TotalSeconds
                        );
                        return ValueTask.CompletedTask;
                    },
                }
            );
        }

        return builder.Build();
    }

    private sealed record GenerateOrUpdateRegulatoryNumberRequest(
        string? Nation,
        int? OrgId,
        int? Year,
        bool Regenerate
    );

    private sealed record AccreditationApplicationResponse(string? AccreditationReference);
}
