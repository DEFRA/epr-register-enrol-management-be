using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolManagementBe.Auth;

namespace EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

/// <summary>
/// RA-448 phase 2: the camelCase, null-omitting JSON contract every call to
/// the operator backend uses. Extracted so <see cref="HttpAccreditationNumberAdapter"/>
/// doesn't carry its own byte-for-byte copy of
/// <c>HttpOperatorBackendPushAdapter.JsonOptions</c> — that adapter is left
/// untouched (already shipped, already tested) and keeps its own instance;
/// this one exists for the new adapter only, so the same wire contract has
/// one definition instead of two.
/// </summary>
internal static class OperatorBackendJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// RA-448 phase 2: the v3 HMAC canonical-payload signing headers every
/// outbound call to the operator backend adds when a shared secret is
/// configured. Extracted so <see cref="HttpAccreditationNumberAdapter"/>
/// doesn't carry its own copy of the block
/// <c>HttpOperatorBackendPushAdapter.BuildRequest</c> already implements —
/// that adapter is left untouched (already shipped, already tested); this
/// helper exists for the new adapter only.
/// </summary>
internal static class OperatorBackendSigning
{
    /// <summary>
    /// Adds <c>x-cdp-client-id</c> unconditionally, then — only when
    /// <paramref name="config"/> has a shared secret configured —
    /// <c>x-cdp-auth-signature</c>/<c>-timestamp</c>/<c>-nonce</c> computed
    /// via <see cref="ClientIdAuthenticationHandler.ComputeSignature"/>, the
    /// same v3 HMAC this service's own inbound handler verifies.
    /// </summary>
    public static void AddHeaders(HttpRequestMessage request, OperatorBackendApiConfig config)
    {
        request.Headers.Add("x-cdp-client-id", config.ClientId);

        if (string.IsNullOrEmpty(config.SharedSecret))
        {
            return;
        }

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var signature = ClientIdAuthenticationHandler.ComputeSignature(
            config.SharedSecret,
            config.ClientId,
            userId: null,
            userName: null,
            timestamp,
            nonce
        );

        request.Headers.Add("x-cdp-auth-signature", signature);
        request.Headers.Add("x-cdp-auth-timestamp", timestamp);
        request.Headers.Add("x-cdp-auth-nonce", nonce);
    }
}
