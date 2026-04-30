using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
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
/// signature in the configured signature header AND a fresh timestamp /
/// single-use nonce. The signature proves the caller knows the shared
/// secret; the timestamp bounds the replay window; the nonce ensures a
/// captured signed request cannot be replayed even within that window.
/// See ADR-0003 for the v2 canonical payload contract.
///
/// When the shared secret is NOT configured the handler fails CLOSED in
/// any non-Development environment (the integrity contract is broken). In
/// Development it falls back to header-trust mode and logs a single
/// warning per process to keep local/BFF-stub workflows ergonomic.
/// </summary>
public class CognitoClientIdAuthenticationHandler(
    IOptionsMonitor<CognitoClientIdAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IHostEnvironment hostEnvironment,
    TimeProvider timeProvider,
    IMemoryCache replayCache)
    : AuthenticationHandler<CognitoClientIdAuthenticationOptions>(options, logger, encoder)
{
    // Tracks whether the Development header-trust downgrade warning has
    // already been emitted in this process. Atomic CAS keeps it to a single
    // log line even under concurrent first requests.
    private static int s_devDowngradeWarned;

    // Cache-key prefix so nonce entries cannot collide with anything else
    // a future caller might park in the shared IMemoryCache instance.
    private const string ReplayCacheKeyPrefix = "cognito-client-id:nonce:";

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

        // Length caps run BEFORE any further processing (in particular,
        // before HMAC compute over attacker-controlled bytes). Each cap is
        // reported with a distinct fail reason so misbehaving clients can
        // be diagnosed in logs. We do NOT silently truncate — the user-id
        // and user-name end up in audit attribution, where corruption
        // would be worse than a 401.
        if (clientId.Length > Options.MaxClientIdLength)
        {
            return Task.FromResult(AuthenticateResult.Fail(
                $"{headerName} exceeds {Options.MaxClientIdLength} chars"));
        }

        string? userId = null;
        string? userName = null;
        string? rolesHeader = null;

        if (Request.Headers.TryGetValue(Options.UserIdHeaderName, out var userIdValues))
        {
            var v = userIdValues.ToString();
            if (v.Length > Options.MaxUserIdLength)
            {
                return Task.FromResult(AuthenticateResult.Fail(
                    $"{Options.UserIdHeaderName} exceeds {Options.MaxUserIdLength} chars"));
            }
            if (!string.IsNullOrWhiteSpace(v)) userId = v;
        }
        if (Request.Headers.TryGetValue(Options.UserNameHeaderName, out var userNameValues))
        {
            var v = userNameValues.ToString();
            if (v.Length > Options.MaxUserNameLength)
            {
                return Task.FromResult(AuthenticateResult.Fail(
                    $"{Options.UserNameHeaderName} exceeds {Options.MaxUserNameLength} chars"));
            }
            if (!string.IsNullOrWhiteSpace(v)) userName = v;
        }
        if (Request.Headers.TryGetValue(Options.UserRolesHeaderName, out var rolesValues))
        {
            var v = rolesValues.ToString();
            if (v.Length > Options.MaxUserRolesLength)
            {
                return Task.FromResult(AuthenticateResult.Fail(
                    $"{Options.UserRolesHeaderName} exceeds {Options.MaxUserRolesLength} chars"));
            }
            if (!string.IsNullOrWhiteSpace(v)) rolesHeader = v;
        }

        // Integrity check: when a shared secret is configured the BFF must
        // sign the trust headers with HMAC-SHA256, supply a fresh timestamp
        // and a single-use nonce. This refuses requests that bypass the BFF
        // and forge identity headers, AND requests that replay a previously
        // captured signed request.
        if (!string.IsNullOrEmpty(Options.SharedSecret))
        {
            // --- Timestamp: present, parseable, within +/- MaxClockSkew. ---
            if (!Request.Headers.TryGetValue(Options.TimestampHeaderName, out var timestampValues))
            {
                return Task.FromResult(AuthenticateResult.Fail(
                    $"Missing {Options.TimestampHeaderName} header"));
            }

            var timestampHeader = timestampValues.ToString();
            if (string.IsNullOrWhiteSpace(timestampHeader))
            {
                return Task.FromResult(AuthenticateResult.Fail(
                    $"Missing {Options.TimestampHeaderName} header"));
            }

            if (timestampHeader.Length > Options.MaxTimestampLength)
            {
                return Task.FromResult(AuthenticateResult.Fail(
                    $"{Options.TimestampHeaderName} exceeds {Options.MaxTimestampLength} chars"));
            }

            if (!DateTimeOffset.TryParse(
                    timestampHeader,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var timestamp))
            {
                return Task.FromResult(AuthenticateResult.Fail(
                    $"Malformed {Options.TimestampHeaderName} header"));
            }

            var now = timeProvider.GetUtcNow();
            if ((now - timestamp).Duration() > Options.MaxClockSkew)
            {
                return Task.FromResult(AuthenticateResult.Fail(
                    $"Stale {Options.TimestampHeaderName} header"));
            }

            // --- Nonce: present. Replay check happens after signature
            // verification so a guessable-nonce attacker cannot lock out
            // legitimate callers by burning their nonces with bad sigs.
            if (!Request.Headers.TryGetValue(Options.NonceHeaderName, out var nonceValues))
            {
                return Task.FromResult(AuthenticateResult.Fail(
                    $"Missing {Options.NonceHeaderName} header"));
            }

            var nonce = nonceValues.ToString();
            if (string.IsNullOrWhiteSpace(nonce))
            {
                return Task.FromResult(AuthenticateResult.Fail(
                    $"Missing {Options.NonceHeaderName} header"));
            }

            if (nonce.Length > Options.MaxNonceLength)
            {
                return Task.FromResult(AuthenticateResult.Fail(
                    $"{Options.NonceHeaderName} exceeds {Options.MaxNonceLength} chars"));
            }

            // --- Signature: matches expected HMAC over v2 canonical string. ---
            if (!Request.Headers.TryGetValue(Options.SignatureHeaderName, out var signatureValues))
            {
                return Task.FromResult(AuthenticateResult.Fail(
                    $"Missing {Options.SignatureHeaderName} header"));
            }

            var providedSignature = signatureValues.ToString();
            // Cap BEFORE HMAC compute: oversize signatures are cheap to
            // detect and computing HMAC over a megabyte of header is itself
            // a small DoS amplifier.
            if (providedSignature.Length > Options.MaxSignatureLength)
            {
                return Task.FromResult(AuthenticateResult.Fail(
                    $"{Options.SignatureHeaderName} exceeds {Options.MaxSignatureLength} chars"));
            }

            var expectedSignature = ComputeSignature(
                Options.SharedSecret!, clientId, userId, userName, rolesHeader,
                timestampHeader, nonce);

            if (!FixedTimeEquals(providedSignature, expectedSignature))
            {
                return Task.FromResult(AuthenticateResult.Fail(
                    $"Invalid {Options.SignatureHeaderName} header"));
            }

            // --- Replay check: the nonce is single-use within its TTL. ---
            var cacheKey = ReplayCacheKeyPrefix + nonce;
            if (replayCache.TryGetValue(cacheKey, out _))
            {
                return Task.FromResult(AuthenticateResult.Fail(
                    $"Replayed {Options.NonceHeaderName} header"));
            }
            replayCache.Set(cacheKey, true, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = Options.ReplayCacheTtl
            });
        }
        else if (!hostEnvironment.IsDevelopment())
        {
            // Fail CLOSED: a missing shared secret outside Development means
            // the integrity contract with the BFF is broken (env var typo,
            // secret rotation race, misconfigured prod). Trusting the
            // headers in this state would let any caller forge identity.
            Logger.LogCritical(
                "CognitoClientIdAuthentication misconfigured: SharedSecret is not set in environment '{Environment}'. Rejecting request — refusing to fall back to header-trust mode outside Development.",
                hostEnvironment.EnvironmentName);
            return Task.FromResult(AuthenticateResult.Fail(
                "Authentication misconfigured: shared secret not set"));
        }
        else if (Interlocked.CompareExchange(ref s_devDowngradeWarned, 1, 0) == 0)
        {
            // Development-only ergonomics: BFF stub mode runs without a
            // shared secret. Emit a single warning per process so the
            // downgrade is visible without spamming the log.
            Logger.LogWarning(
                "CognitoClientIdAuthentication: SharedSecret not configured — operating in header-trust mode. This is allowed only because the host environment is '{Environment}'. Set Auth:SharedSecret in any non-Development deployment.",
                hostEnvironment.EnvironmentName);
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
    /// Surface the <see cref="AuthenticateResult.Failure"/> message via the
    /// standard <c>WWW-Authenticate</c> challenge so operators (and tests)
    /// can diagnose misbehaving clients without needing log access. Only
    /// the failure message — which is authored entirely server-side — is
    /// echoed; no attacker-supplied bytes leak back into the response.
    /// </summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // Re-run authenticate to pick up the failure message stored on the
        // request feature; AuthenticationMiddleware does not pass it through.
        var authResult = await Context.AuthenticateAsync(Scheme.Name);
        var failureMessage = authResult.Failure?.Message;

        Response.StatusCode = 401;
        var challenge = string.IsNullOrEmpty(failureMessage)
            ? Scheme.Name
            : $"{Scheme.Name} error=\"invalid_request\", error_description=\"{EscapeQuotedString(failureMessage)}\"";
        Response.Headers.Append("WWW-Authenticate", challenge);
    }

    private static string EscapeQuotedString(string value)
    {
        // RFC 7235 quoted-string escaping: backslash and double-quote.
        // Cap the surfaced length defensively even though the source is a
        // server-authored constant template.
        var trimmed = value.Length > 256 ? value[..256] : value;
        return trimmed.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>
    /// Canonical signing payload (v2). Order and field separators are part
    /// of the contract with the BFF: any change here is a breaking change
    /// and requires a coordinated deploy. The timestamp and nonce are
    /// non-optional — see ADR-0003.
    /// </summary>
    internal static string ComputeSignature(
        string sharedSecret,
        string clientId,
        string? userId,
        string? userName,
        string? userRoles,
        string timestamp,
        string nonce)
    {
        var payload = string.Join('\n',
            "v2",
            clientId,
            userId ?? string.Empty,
            userName ?? string.Empty,
            userRoles ?? string.Empty,
            timestamp,
            nonce);
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
