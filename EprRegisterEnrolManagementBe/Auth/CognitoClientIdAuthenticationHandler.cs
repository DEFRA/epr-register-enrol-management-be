using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolManagementBe.Auth;

/// <summary>
/// Authentication handler that accepts a CDP Cognito client ID supplied in a
/// request header. CDP validates the upstream service's JWT before forwarding
/// the request, so the presence of the header is sufficient — no further
/// authorisation is performed.
///
/// When <see cref="CognitoClientIdAuthenticationOptions.SharedSecret"/> is
/// configured, requests must additionally carry a valid HMAC-SHA256
/// signature in the configured signature header. The signature is computed
/// by the BFF over a canonical string of the trust headers and proves the
/// caller knows the shared secret — defending against requests that reach
/// the backend directly and forge trust headers.
/// </summary>
public class CognitoClientIdAuthenticationHandler(
    IOptionsMonitor<CognitoClientIdAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<CognitoClientIdAuthenticationOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headerName = Options.HeaderName;

        if (!Request.Headers.TryGetValue(headerName, out var values))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var clientId = values.ToString();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return Task.FromResult(AuthenticateResult.Fail($"Empty {headerName} header"));
        }

        string? userId = null;
        string? userName = null;
        string? rolesHeader = null;

        if (Request.Headers.TryGetValue(Options.UserIdHeaderName, out var userIdValues))
        {
            var v = userIdValues.ToString();
            if (!string.IsNullOrWhiteSpace(v)) userId = v;
        }
        if (Request.Headers.TryGetValue(Options.UserNameHeaderName, out var userNameValues))
        {
            var v = userNameValues.ToString();
            if (!string.IsNullOrWhiteSpace(v)) userName = v;
        }
        if (Request.Headers.TryGetValue(Options.UserRolesHeaderName, out var rolesValues))
        {
            var v = rolesValues.ToString();
            if (!string.IsNullOrWhiteSpace(v)) rolesHeader = v;
        }

        // Integrity check: when a shared secret is configured the BFF must
        // sign the trust headers with HMAC-SHA256 so we refuse requests that
        // reach the backend directly and forge identity headers.
        if (!string.IsNullOrEmpty(Options.SharedSecret))
        {
            if (!Request.Headers.TryGetValue(Options.SignatureHeaderName, out var signatureValues))
            {
                return Task.FromResult(AuthenticateResult.Fail(
                    $"Missing {Options.SignatureHeaderName} header"));
            }

            var providedSignature = signatureValues.ToString();
            var expectedSignature = ComputeSignature(
                Options.SharedSecret!, clientId, userId, userName, rolesHeader);

            if (!FixedTimeEquals(providedSignature, expectedSignature))
            {
                return Task.FromResult(AuthenticateResult.Fail(
                    $"Invalid {Options.SignatureHeaderName} header"));
            }
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, clientId),
            new("cognito:client_id", clientId)
        };

        // The BFF (frontend) forwards the acting user's identity and role
        // membership in optional headers. They are not authenticators in
        // their own right — CDP has already validated the upstream JWT and
        // placed the trusted client id in the primary header — but they let
        // backend endpoints make role-based decisions and produce more
        // useful audit log lines without a separate user lookup.
        if (userId is not null) claims.Add(new Claim("user:id", userId));
        if (userName is not null) claims.Add(new Claim("user:name", userName));
        if (rolesHeader is not null)
        {
            foreach (var role in rolesHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <summary>
    /// Canonical signing payload. Order and field separators are part of the
    /// contract with the BFF: any change here is a breaking change and
    /// requires a coordinated deploy.
    /// </summary>
    internal static string ComputeSignature(
        string sharedSecret,
        string clientId,
        string? userId,
        string? userName,
        string? userRoles)
    {
        var payload = string.Join('\n',
            "v1",
            clientId,
            userId ?? string.Empty,
            userName ?? string.Empty,
            userRoles ?? string.Empty);
        var keyBytes = Encoding.UTF8.GetBytes(sharedSecret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var mac = HMACSHA256.HashData(keyBytes, payloadBytes);
        return Convert.ToBase64String(mac);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ab.Length == bb.Length
            && CryptographicOperations.FixedTimeEquals(ab, bb);
    }
}
