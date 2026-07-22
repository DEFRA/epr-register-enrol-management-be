using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolManagementBe.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
/// <b>Contract TBC with the OBE-2 owner (RA-311 plan §4):</b> the relative
/// path and request shape below are a placeholder pending agreement on the
/// exact MBE-1 ↔ OBE-2 push contract. Confined to this one file/constant/DTO
/// so it is a small, isolated change once the real contract lands.
/// </summary>
internal sealed class HttpOperatorBackendPushAdapter(
    IHttpClientFactory httpClientFactory,
    IOptions<OperatorBackendApiConfig> config,
    ILogger<HttpOperatorBackendPushAdapter> logger) : IOperatorBackendPushAdapter
{
    // Contract TBC with OBE-2 owner — RA-311 plan §4.
    private const string RelativePath = "/case-working/re-accreditation/query-raised";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly OperatorBackendApiConfig _config = config.Value;

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

        var endpoint = $"{_config.Url.TrimEnd('/')}{RelativePath}";

        try
        {
            using var request = BuildRequest(endpoint, workItemId, queryNote, sectionKeys);
            var client = httpClientFactory.CreateClient("DefaultClient");
            var response = await client.SendAsync(request, cancellationToken);

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

    private HttpRequestMessage BuildRequest(
        string endpoint, Guid workItemId, string queryNote, IReadOnlyList<string> sectionKeys)
    {
        var body = new QueryRaisedPushRequest(workItemId, queryNote, sectionKeys);
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

    private sealed record QueryRaisedPushRequest(Guid WorkItemId, string QueryNote, IReadOnlyList<string> SectionKeys);
}
