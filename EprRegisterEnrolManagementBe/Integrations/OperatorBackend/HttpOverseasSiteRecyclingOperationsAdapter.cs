using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

/// <summary>
/// RA-469 AC16 real <see cref="IOverseasSiteRecyclingOperationsAdapter"/>.
///
/// Reuses <see cref="OperatorBackendApiConfig"/>, the same unproxied
/// "DefaultClient", and the shared <see cref="OperatorBackendJson"/> /
/// <see cref="OperatorBackendSigning"/> helpers <see cref="HttpAccreditationNumberAdapter"/>
/// already uses for the wire contract and v3 HMAC signing — same backend
/// service, different route.
///
/// Calls the regulator-scoped contract:
/// <c>PATCH api/v1/accreditation-applications/{organisationId}/{applicationId}/overseas-sites/{siteId}/recycling-operations</c>
/// with <c>{ operationCodes: [...] }</c> as the body. Unlike
/// <see cref="HttpAccreditationNumberAdapter"/>, which signs with a null
/// identity, this adapter signs with the REAL caller's user id/name (via
/// <see cref="OperatorBackendSigning.AddHeaders"/>'s optional
/// userId/userName parameters) so the backend's audit record (AC15/AC19)
/// attributes the edit to the acting regulator.
///
/// Registered unconditionally like <see cref="HttpAccreditationNumberAdapter"/>
/// (there is no safe no-op for a regulator edit to depend on), so it
/// enforces the Enabled=false behaviour-neutral invariant (MBE-F5) itself:
/// an environment with Url pre-populated ahead of turning Enabled on must
/// not have this adapter start firing real signed HTTP calls anyway.
///
/// Retries transient failures (5xx / transport exceptions) with the same
/// firm, capped worst-case budget as <see cref="HttpAccreditationNumberAdapter"/>
/// (<see cref="MaxRetryAttempts"/> attempts, <see cref="PerAttemptTimeoutSeconds"/>s
/// each, backoff capped at <see cref="s_maxBackoff"/>) — this call sits on
/// a synchronous, regulator-facing edit request, so its worst case must
/// stay small and predictable. Never retries a 4xx.
/// </summary>
internal sealed class HttpOverseasSiteRecyclingOperationsAdapter(
    IHttpClientFactory httpClientFactory,
    IOptions<OperatorBackendApiConfig> config,
    ILogger<HttpOverseasSiteRecyclingOperationsAdapter> logger,
    ResiliencePipeline<HttpResponseMessage>? retryPipeline = null
) : IOverseasSiteRecyclingOperationsAdapter
{
    private const string RelativePathTemplate =
        "/api/v1/accreditation-applications/{0}/{1}/overseas-sites/{2}/recycling-operations";

    // Deliberately local to this adapter, not read from OperatorBackendApiConfig
    // — mirrors HttpAccreditationNumberAdapter's reasoning: this budget must
    // stay firm and decoupled from the best-effort push's shared timeout knob.
    private const int MaxRetryAttempts = 2;
    private const int PerAttemptTimeoutSeconds = 5;
    private static readonly TimeSpan s_maxBackoff = TimeSpan.FromSeconds(2);

    private readonly OperatorBackendApiConfig _config = config.Value;
    private readonly ResiliencePipeline<HttpResponseMessage> _retryPipeline =
        retryPipeline ?? BuildRetryPipeline(logger);

    public async Task<OverseasSiteRecyclingOperationsResult> UpdateRecyclingOperationsAsync(
        OverseasSiteRecyclingOperationsRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (!_config.Enabled)
        {
            return OverseasSiteRecyclingOperationsResult.TransientFailure(
                "OperatorBackendApi:Enabled is false."
            );
        }

        if (string.IsNullOrWhiteSpace(_config.Url))
        {
            return OverseasSiteRecyclingOperationsResult.TransientFailure(
                "OperatorBackendApi:Url is not configured."
            );
        }

        var relativePath = string.Format(
            RelativePathTemplate,
            Uri.EscapeDataString(request.OrganisationId),
            Uri.EscapeDataString(request.ApplicationId),
            Uri.EscapeDataString(request.SiteId)
        );
        var endpoint = $"{_config.Url.TrimEnd('/')}{relativePath}";

        logger.LogInformation(
            "Updating recycling operations for organisation {OrganisationId} application "
                + "{ApplicationId} site {SiteId} from {Endpoint} (correlation {CorrelationId})",
            request.OrganisationId,
            request.ApplicationId,
            request.SiteId,
            endpoint,
            request.CorrelationId
        );

        var body = new BackendRequestBody(request.OperationCodes);

        try
        {
            var response = await _retryPipeline.ExecuteAsync(
                async ct =>
                {
                    // Rebuilt on every attempt (retry included): request content
                    // can only be sent once, and a fresh timestamp/nonce per
                    // attempt is correct anyway (signature window).
                    using var httpRequest = BuildRequest(
                        endpoint,
                        request.CorrelationId,
                        body,
                        request.UserId,
                        request.UserName
                    );
                    var client = httpClientFactory.CreateClient("DefaultClient");
                    return await client.SendAsync(httpRequest, ct);
                },
                cancellationToken
            );

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogWarning(
                    "Backend returned 404 from {Endpoint} for organisation {OrganisationId} "
                        + "application {ApplicationId} site {SiteId} (correlation {CorrelationId}).",
                    endpoint,
                    request.OrganisationId,
                    request.ApplicationId,
                    request.SiteId,
                    request.CorrelationId
                );
                return OverseasSiteRecyclingOperationsResult.NotFound();
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                var conflictBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "Backend returned 409 from {Endpoint} for organisation {OrganisationId} "
                        + "application {ApplicationId} site {SiteId} (correlation {CorrelationId}): {Body}",
                    endpoint,
                    request.OrganisationId,
                    request.ApplicationId,
                    request.SiteId,
                    request.CorrelationId,
                    conflictBody
                );
                return OverseasSiteRecyclingOperationsResult.Conflict(
                    string.IsNullOrWhiteSpace(conflictBody)
                        ? $"Backend returned 409 from {endpoint}."
                        : conflictBody
                );
            }

            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                var validationBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "Backend returned 400 from {Endpoint} for organisation {OrganisationId} "
                        + "application {ApplicationId} site {SiteId} (correlation {CorrelationId}): {Body}",
                    endpoint,
                    request.OrganisationId,
                    request.ApplicationId,
                    request.SiteId,
                    request.CorrelationId,
                    validationBody
                );
                return ParseValidationFailure(validationBody, logger);
            }

            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError(
                    "Backend returned {Status} from {Endpoint} for organisation {OrganisationId} "
                        + "application {ApplicationId} site {SiteId} (correlation {CorrelationId}): {Body}",
                    (int)response.StatusCode,
                    endpoint,
                    request.OrganisationId,
                    request.ApplicationId,
                    request.SiteId,
                    request.CorrelationId,
                    responseBody
                );
                return OverseasSiteRecyclingOperationsResult.TransientFailure(
                    $"Backend returned {(int)response.StatusCode} from {endpoint}."
                );
            }

            var siteJson = await response.Content.ReadAsStringAsync(cancellationToken);

            logger.LogInformation(
                "Recycling operations updated for organisation {OrganisationId} application "
                    + "{ApplicationId} site {SiteId} (correlation {CorrelationId}).",
                request.OrganisationId,
                request.ApplicationId,
                request.SiteId,
                request.CorrelationId
            );
            return OverseasSiteRecyclingOperationsResult.Success(siteJson);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's own token was cancelled (request aborted, upstream
            // timeout) — this is not a backend failure, so it must propagate
            // as a cancellation rather than being reported as a
            // TransientFailure result the caller would otherwise treat as a
            // definite, retriable business outcome.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to update recycling operations for organisation {OrganisationId} application "
                    + "{ApplicationId} site {SiteId} from {Endpoint} (correlation {CorrelationId})",
                request.OrganisationId,
                request.ApplicationId,
                request.SiteId,
                endpoint,
                request.CorrelationId
            );
            return OverseasSiteRecyclingOperationsResult.TransientFailure(ex.Message);
        }
    }

    /// <summary>
    /// Parses the backend's ProblemDetails-shaped 400 body for
    /// <c>errorCode</c>/<c>field</c>/<c>detail</c> (the same extensions
    /// convention <c>DulyMake</c>/<c>LogDecision</c> use). A malformed or
    /// unparseable body still surfaces as a validation failure — never a
    /// TransientFailure — since the backend already told us the request
    /// itself was rejected as invalid; ValidationFailed's own fallback
    /// ("validation-failed") covers the missing-errorCode case.
    /// </summary>
    private static OverseasSiteRecyclingOperationsResult ParseValidationFailure(
        string body,
        ILogger logger
    )
    {
        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<ProblemResponseBody>(
                body,
                OperatorBackendJson.Options
            );
            return OverseasSiteRecyclingOperationsResult.ValidationFailed(
                parsed?.ErrorCode,
                parsed?.Field,
                parsed?.Detail
            );
        }
        catch (System.Text.Json.JsonException ex)
        {
            // The backend already told us the request was rejected as
            // invalid (400) — a malformed/unparseable body still surfaces
            // as a validation failure (never a TransientFailure), but the
            // parse failure itself is logged so an unexpected backend body
            // shape is visible in operations rather than silently masked.
            logger.LogWarning(
                ex,
                "Backend 400 body could not be parsed as a ProblemDetails-shaped errorCode/field response."
            );
            return OverseasSiteRecyclingOperationsResult.ValidationFailed(null, null, null);
        }
    }

    private HttpRequestMessage BuildRequest<TBody>(
        string endpoint,
        Guid correlationId,
        TBody body,
        string? userId,
        string? userName
    )
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, endpoint)
        {
            Content = JsonContent.Create(body, options: OperatorBackendJson.Options),
        };

        request.Headers.Add("X-Correlation-Id", correlationId.ToString());
        // RA-469: signs with the REAL caller's identity (not null/null like
        // HttpAccreditationNumberAdapter) — see the class doc comment.
        OperatorBackendSigning.AddHeaders(request, _config, userId, userName);

        return request;
    }

    /// <summary>
    /// <see cref="MaxRetryAttempts"/> retries, jittered exponential backoff
    /// capped at <see cref="s_maxBackoff"/>, each attempt bounded by
    /// <see cref="PerAttemptTimeoutSeconds"/>. Retries transport exceptions,
    /// per-attempt timeouts, and 5xx responses only — never a 4xx, which is
    /// most likely a validation/not-found/conflict outcome a retry would not
    /// fix. Mirrors <see cref="HttpAccreditationNumberAdapter.BuildRetryPipeline"/>.
    /// </summary>
    private static ResiliencePipeline<HttpResponseMessage> BuildRetryPipeline(ILogger logger) =>
        OperatorBackendRetryPipeline.Build(
            logger,
            "Recycling operations update",
            MaxRetryAttempts,
            PerAttemptTimeoutSeconds,
            s_maxBackoff
        );

    private sealed record BackendRequestBody(IReadOnlyList<string> OperationCodes);

    private sealed record ProblemResponseBody(string? Detail, string? ErrorCode, string? Field);
}
