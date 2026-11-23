using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Utils.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

/// <summary>
/// RA-769: the real <see cref="IAccreditationSummaryAdapter"/>. Same configuration
/// (<see cref="OperatorBackendApiConfig"/>), unproxied "DefaultClient", request signing and retry pipeline as
/// <see cref="HttpAccreditationNumberAdapter"/>, which it follows. Signs with a null identity: a read has no
/// per-user audit need.
///
/// Registered unconditionally, so it enforces the Enabled=false invariant itself (as
/// <see cref="HttpAccreditationNumberAdapter"/> does): <c>OperatorBackendApi:Enabled=false</c> is a transient
/// failure, which the tab shows as "not available", never a call. This sits on a synchronous caseworker page
/// load, so the retry budget is the same small, firm one (<see cref="MaxRetryAttempts"/> retries of
/// <see cref="PerAttemptTimeoutSeconds"/>s each). A 4xx is never retried.
/// </summary>
internal sealed class HttpAccreditationSummaryAdapter(
    IHttpClientFactory httpClientFactory,
    IOptions<OperatorBackendApiConfig> config,
    ILogger<HttpAccreditationSummaryAdapter> logger,
    IStructuredLogger<HttpAccreditationSummaryAdapter> structuredLogger,
    ResiliencePipeline<HttpResponseMessage>? retryPipeline = null
) : IAccreditationSummaryAdapter
{
    private const string RelativePathTemplate =
        "/api/v1/accreditation-applications/case-management/{0}/{1}/accreditation-summary";

    private const int MaxRetryAttempts = 2;
    private const int PerAttemptTimeoutSeconds = 5;
    private static readonly TimeSpan s_maxBackoff = TimeSpan.FromSeconds(2);

    private readonly OperatorBackendApiConfig _config = config.Value;
    private readonly ResiliencePipeline<HttpResponseMessage> _retryPipeline =
        retryPipeline
        ?? OperatorBackendRetryPipeline.Build(
            logger,
            "Accreditation summary read",
            MaxRetryAttempts,
            PerAttemptTimeoutSeconds,
            s_maxBackoff
        );

    public async Task<AccreditationSummaryReadResult> GetAccreditationSummaryAsync(
        AccreditationSummaryReadRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (!_config.Enabled)
        {
            return AccreditationSummaryReadResult.TransientFailure("OperatorBackendApi:Enabled is false.");
        }

        if (string.IsNullOrWhiteSpace(_config.Url))
        {
            return AccreditationSummaryReadResult.TransientFailure("OperatorBackendApi:Url is not configured.");
        }

        var endpoint =
            _config.Url.TrimEnd('/')
            + string.Format(
                RelativePathTemplate,
                Uri.EscapeDataString(request.OrganisationId),
                Uri.EscapeDataString(request.ApplicationId)
            );

        try
        {
            var response = await _retryPipeline.ExecuteAsync(
                async ct =>
                {
                    // Rebuilt on every attempt: a fresh timestamp and nonce each time is correct anyway.
                    using var httpRequest = BuildRequest(endpoint, request.CorrelationId);
                    var client = httpClientFactory.CreateClient("DefaultClient");
                    return await client.SendAsync(httpRequest, ct);
                },
                cancellationToken
            );

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation(
                    "Operator backend holds no Approved accreditation at {Endpoint} (correlation {CorrelationId}).",
                    endpoint,
                    request.CorrelationId
                );
                return AccreditationSummaryReadResult.NotFound();
            }

            if (!response.IsSuccessStatusCode)
            {
                structuredLogger.Log(
                    LogLevel.Error,
                    "Operator backend returned non-success status for accreditation summary read",
                    new Dictionary<string, object?>
                    {
                        ["http.request.endpoint"] = endpoint,
                        ["organisation.id"] = request.OrganisationId,
                        ["application.id"] = request.ApplicationId,
                        ["correlation.id"] = request.CorrelationId,
                        ["http.response.status_code"] = (int)response.StatusCode,
                    }
                );
                return AccreditationSummaryReadResult.TransientFailure(
                    $"Operator backend returned {(int)response.StatusCode} from {endpoint}."
                );
            }

            var summary = await ReadSummaryAsync(response, endpoint, request.CorrelationId, cancellationToken);
            return summary is { IsUsable: true }
                ? AccreditationSummaryReadResult.Success(summary)
                : AccreditationSummaryReadResult.TransientFailure("Operator backend returned an unusable summary.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's own token: a cancellation, not a backend failure.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to read the accreditation summary for organisation {OrganisationId} application "
                    + "{ApplicationId} from {Endpoint} (correlation {CorrelationId})",
                request.OrganisationId,
                request.ApplicationId,
                endpoint,
                request.CorrelationId
            );
            return AccreditationSummaryReadResult.TransientFailure(ex.Message);
        }
    }

    // A body that is not JSON, or lacks a required field, is unusable rather than an exception.
    private async Task<OperatorAccreditationSummary?> ReadSummaryAsync(
        HttpResponseMessage response,
        string endpoint,
        Guid correlationId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<OperatorAccreditationSummary>(
                OperatorBackendJson.Options,
                cancellationToken
            );
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            logger.LogError(
                ex,
                "Accreditation summary from {Endpoint} could not be read (correlation {CorrelationId})",
                endpoint,
                correlationId
            );
            return null;
        }
    }

    private HttpRequestMessage BuildRequest(string endpoint, Guid correlationId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Add("X-Correlation-Id", correlationId.ToString());
        OperatorBackendSigning.AddHeaders(request, _config);
        return request;
    }
}
