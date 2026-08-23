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
/// Authentication handler that accepts a CDP client ID supplied in a
/// request header. Despite the header's original name, this is NOT a CDP-
/// verified identity — CDP does not validate a JWT or inject this header
/// itself; the caller (management-fe or epr-register-enrol-backend) asserts
/// its own client ID and proves it via the HMAC signature described below.
///
/// When <see cref="ClientIdAuthenticationOptions.ClientSecrets"/> is
/// non-empty, requests must additionally carry a valid HMAC-SHA256
/// signature in the configured signature header AND a fresh timestamp /
/// single-use nonce. The signature proves the caller knows the secret
/// registered for the clientId it asserted (RA-345: each known caller has
/// its own secret rather than one value shared by all callers); the
/// timestamp bounds the replay window; the nonce ensures a captured signed
/// request cannot be replayed even within that window. See ADR-0003 for
/// the timestamp/nonce contract and ADR-0005 for the original v3 payload
/// (role membership dropped — authorization was entirely the BFF's
/// concern) and its "Follow-up: per-caller shared secrets" section for
/// the rationale behind this per-clientId lookup. RA-469 reinstates a
/// role field (and adds nation) in the signed payload: this endpoint's
/// AC17 authorization is deliberately enforced backend-side too, so the
/// two claims it gates on must carry the same signed-integrity guarantee
/// as userId/userName rather than arriving as bare, tamperable headers.
/// Callers that have no role/nation to send — epr-register-enrol-backend,
/// in both directions — keep signing the original six-line v3 form, and
/// this handler accepts either form when no role/nation header is present;
/// see ADR-0007 and <see cref="VerifySignature"/>.
///
/// When no client secrets are configured the handler fails CLOSED in any
/// non-Development environment (the integrity contract is broken). In
/// Development it falls back to header-trust mode and logs a single
/// warning per process to keep local/BFF-stub workflows ergonomic.
/// </summary>
public class ClientIdAuthenticationHandler(
    IOptionsMonitor<ClientIdAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IHostEnvironment hostEnvironment,
    TimeProvider timeProvider,
    IMemoryCache replayCache
) : AuthenticationHandler<ClientIdAuthenticationOptions>(options, logger, encoder)
{
    // Tracks whether the Development header-trust downgrade warning has
    // already been emitted in this process. Atomic CAS keeps it to a single
    // log line even under concurrent first requests.
    private static int s_devDowngradeWarned;

    // Cache-key prefix so nonce entries cannot collide with anything else
    // a future caller might park in the shared IMemoryCache instance.
    private const string ReplayCacheKeyPrefix = "client-id:nonce:";

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
            return Task.FromResult(
                AuthenticateResult.Fail($"{headerName} exceeds {Options.MaxClientIdLength} chars")
            );
        }

        string? userId = null;
        string? userName = null;
        string? role = null;
        string? nation = null;

        if (Request.Headers.TryGetValue(Options.UserIdHeaderName, out var userIdValues))
        {
            var v = userIdValues.ToString();
            if (v.Length > Options.MaxUserIdLength)
            {
                return Task.FromResult(
                    AuthenticateResult.Fail(
                        $"{Options.UserIdHeaderName} exceeds {Options.MaxUserIdLength} chars"
                    )
                );
            }
            if (!string.IsNullOrWhiteSpace(v))
                userId = v;
        }
        if (Request.Headers.TryGetValue(Options.UserNameHeaderName, out var userNameValues))
        {
            var v = userNameValues.ToString();
            if (v.Length > Options.MaxUserNameLength)
            {
                return Task.FromResult(
                    AuthenticateResult.Fail(
                        $"{Options.UserNameHeaderName} exceeds {Options.MaxUserNameLength} chars"
                    )
                );
            }
            if (!string.IsNullOrWhiteSpace(v))
                userName = v;
        }
        // RA-469: role/nation trust headers follow the exact same
        // optional-header pattern as userId/userName above, and (like
        // userId/userName) are folded into the v3 HMAC canonical payload —
        // see ComputeSignature's doc comment — so a tampered role/nation
        // invalidates the signature the same way a tampered userId would.
        if (Request.Headers.TryGetValue(Options.RoleHeaderName, out var roleValues))
        {
            var v = roleValues.ToString();
            if (v.Length > Options.MaxUserRoleLength)
            {
                return Task.FromResult(
                    AuthenticateResult.Fail(
                        $"{Options.RoleHeaderName} exceeds {Options.MaxUserRoleLength} chars"
                    )
                );
            }
            if (!string.IsNullOrWhiteSpace(v))
                role = v;
        }
        if (Request.Headers.TryGetValue(Options.NationHeaderName, out var nationValues))
        {
            var v = nationValues.ToString();
            if (v.Length > Options.MaxUserNationLength)
            {
                return Task.FromResult(
                    AuthenticateResult.Fail(
                        $"{Options.NationHeaderName} exceeds {Options.MaxUserNationLength} chars"
                    )
                );
            }
            if (!string.IsNullOrWhiteSpace(v))
                nation = v;
        }

        // Integrity check: when per-caller secrets are configured the BFF
        // must sign the trust headers with HMAC-SHA256, supply a fresh
        // timestamp and a single-use nonce. This refuses requests that
        // bypass the BFF and forge identity headers, AND requests that
        // replay a previously captured signed request.
        if (Options.ClientSecrets.Count > 0)
        {
            // --- Timestamp: present, parseable, within +/- MaxClockSkew. ---
            if (!Request.Headers.TryGetValue(Options.TimestampHeaderName, out var timestampValues))
            {
                return Task.FromResult(
                    AuthenticateResult.Fail($"Missing {Options.TimestampHeaderName} header")
                );
            }

            var timestampHeader = timestampValues.ToString();
            if (string.IsNullOrWhiteSpace(timestampHeader))
            {
                return Task.FromResult(
                    AuthenticateResult.Fail($"Missing {Options.TimestampHeaderName} header")
                );
            }

            if (timestampHeader.Length > Options.MaxTimestampLength)
            {
                return Task.FromResult(
                    AuthenticateResult.Fail(
                        $"{Options.TimestampHeaderName} exceeds {Options.MaxTimestampLength} chars"
                    )
                );
            }

            if (
                !DateTimeOffset.TryParse(
                    timestampHeader,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var timestamp
                )
            )
            {
                return Task.FromResult(
                    AuthenticateResult.Fail($"Malformed {Options.TimestampHeaderName} header")
                );
            }

            var now = timeProvider.GetUtcNow();
            if ((now - timestamp).Duration() > Options.MaxClockSkew)
            {
                return Task.FromResult(
                    AuthenticateResult.Fail($"Stale {Options.TimestampHeaderName} header")
                );
            }

            // --- Nonce: present. Replay check happens after signature
            // verification so a guessable-nonce attacker cannot lock out
            // legitimate callers by burning their nonces with bad sigs.
            if (!Request.Headers.TryGetValue(Options.NonceHeaderName, out var nonceValues))
            {
                return Task.FromResult(
                    AuthenticateResult.Fail($"Missing {Options.NonceHeaderName} header")
                );
            }

            var nonce = nonceValues.ToString();
            if (string.IsNullOrWhiteSpace(nonce))
            {
                return Task.FromResult(
                    AuthenticateResult.Fail($"Missing {Options.NonceHeaderName} header")
                );
            }

            if (nonce.Length > Options.MaxNonceLength)
            {
                return Task.FromResult(
                    AuthenticateResult.Fail(
                        $"{Options.NonceHeaderName} exceeds {Options.MaxNonceLength} chars"
                    )
                );
            }

            // --- Signature: matches expected HMAC over v3 canonical string. ---
            if (!Request.Headers.TryGetValue(Options.SignatureHeaderName, out var signatureValues))
            {
                return Task.FromResult(
                    AuthenticateResult.Fail($"Missing {Options.SignatureHeaderName} header")
                );
            }

            var providedSignature = signatureValues.ToString();
            // Cap BEFORE HMAC compute: oversize signatures are cheap to
            // detect and computing HMAC over a megabyte of header is itself
            // a small DoS amplifier.
            if (providedSignature.Length > Options.MaxSignatureLength)
            {
                return Task.FromResult(
                    AuthenticateResult.Fail(
                        $"{Options.SignatureHeaderName} exceeds {Options.MaxSignatureLength} chars"
                    )
                );
            }

            // --- Client secret lookup: the secret used to verify the
            // signature is the one registered for the clientId the caller
            // asserted (RA-345). An unrecognized clientId gets the same
            // externally-visible failure MESSAGE as a bad signature, so the
            // response body/WWW-Authenticate header alone can't be used to
            // probe which client ids are known — but is logged distinctly
            // so operators can tell the two failure modes apart. This does
            // NOT close the lookup as a timing side-channel: an unrecognized
            // clientId returns immediately, while a known one goes on to
            // compute an HMAC before failing, so response latency alone
            // could in principle distinguish the two. Accepted as
            // negligible here because the only client ids that could ever
            // be confirmed "known" this way are the two documented public
            // defaults (frontend, epr-register-enrol-backend) — there is
            // nothing secret left to enumerate.
            if (!Options.ClientSecrets.TryGetValue(clientId, out var secret))
            {
                Logger.LogWarning(
                    "ClientIdAuthentication: no secret registered for asserted client id {ClientId}",
                    clientId
                );
                return Task.FromResult(
                    AuthenticateResult.Fail($"Invalid {Options.SignatureHeaderName} header")
                );
            }

            var signaturePayload = new ClientIdSignaturePayload(
                clientId,
                userId,
                userName,
                timestampHeader,
                nonce,
                role,
                nation
            );

            if (!VerifySignature(secret, signaturePayload, providedSignature))
            {
                Logger.LogWarning(
                    "ClientIdAuthentication: signature mismatch for asserted client id {ClientId}",
                    clientId
                );
                return Task.FromResult(
                    AuthenticateResult.Fail($"Invalid {Options.SignatureHeaderName} header")
                );
            }

            // --- Replay check: the nonce is single-use within its TTL. ---
            var cacheKey = ReplayCacheKeyPrefix + nonce;
            if (replayCache.TryGetValue(cacheKey, out _))
            {
                return Task.FromResult(
                    AuthenticateResult.Fail($"Replayed {Options.NonceHeaderName} header")
                );
            }
            replayCache.Set(
                cacheKey,
                true,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = Options.ReplayCacheTtl,
                }
            );
        }
        else if (!hostEnvironment.IsDevelopment())
        {
            // Fail CLOSED: no configured client secrets outside Development
            // means the integrity contract with the BFF is broken (env var
            // typo, secret rotation race, misconfigured prod). Trusting the
            // headers in this state would let any caller forge identity.
            Logger.LogCritical(
                "ClientIdAuthentication misconfigured: no ClientSecrets are set in environment '{Environment}'. Rejecting request — refusing to fall back to header-trust mode outside Development.",
                hostEnvironment.EnvironmentName
            );
            return Task.FromResult(
                AuthenticateResult.Fail("Authentication misconfigured: no client secrets set")
            );
        }
        else if (Interlocked.CompareExchange(ref s_devDowngradeWarned, 1, 0) == 0)
        {
            // Development-only ergonomics: BFF stub mode runs without any
            // client secrets configured. Emit a single warning per process
            // so the downgrade is visible without spamming the log.
            Logger.LogWarning(
                "ClientIdAuthentication: no ClientSecrets configured — operating in header-trust mode. This is allowed only because the host environment is '{Environment}'. Set AUTH_SHARED_SECRET__MANAGEMENT_FE / AUTH_SHARED_SECRET__BACKEND in any non-Development deployment.",
                hostEnvironment.EnvironmentName
            );
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, clientId),
            new("client_id", clientId),
        };

        // The BFF (frontend) forwards the acting user's identity in optional
        // headers. They are not authenticators in their own right — the HMAC
        // signature above is what establishes trust in the primary client id
        // header — but they let backend endpoints produce more useful audit
        // log lines without a separate user lookup.
        if (userId is not null)
            claims.Add(new Claim("user:id", userId));
        if (userName is not null)
            claims.Add(new Claim("user:name", userName));
        if (role is not null)
            claims.Add(new Claim("user:role", role));
        if (nation is not null)
            claims.Add(new Claim("user:nation", nation));

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

        // Log all incoming auth-relevant headers so operators can diagnose
        // 401s without needing to correlate with the caller's own logs.
        // The signature value is intentionally omitted — it is an HMAC
        // digest, not a secret, but there is no diagnostic value in logging it.
        Logger.LogWarning(
            "ClientId auth challenge on {Method} {Path}: {FailureReason}. "
                + "Headers — {ClientIdHeader}: {ClientId}, "
                + "{TimestampHeader}: {Timestamp}, "
                + "{NonceHeader}: {Nonce}, "
                + "{SignatureHeader} present: {SignaturePresent}, "
                + "Content-Type: {ContentType}, Content-Length: {ContentLength}",
            Request.Method,
            Request.Path,
            failureMessage ?? "(no failure message)",
            Options.HeaderName,
            Request.Headers.TryGetValue(Options.HeaderName, out var clientId)
                ? clientId.ToString()
                : "(absent)",
            Options.TimestampHeaderName,
            Request.Headers.TryGetValue(Options.TimestampHeaderName, out var ts)
                ? ts.ToString()
                : "(absent)",
            Options.NonceHeaderName,
            Request.Headers.TryGetValue(Options.NonceHeaderName, out var nonce)
                ? nonce.ToString()
                : "(absent)",
            Options.SignatureHeaderName,
            Request.Headers.ContainsKey(Options.SignatureHeaderName),
            Request.ContentType ?? "(absent)",
            Request.ContentLength?.ToString() ?? "(absent)"
        );

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
    /// Canonical signing payload (v3). Order and field separators are part
    /// of the contract with EVERY caller — management-fe and
    /// epr-register-enrol-backend, the latter in both directions — so any
    /// change here is a breaking change that needs a coordinated deploy of
    /// all three services (ADR-0007 records the dev outage that made this
    /// explicit). The timestamp and nonce are non-optional — see ADR-0003.
    ///
    /// Two wire forms exist (ADR-0007):
    /// <list type="bullet">
    /// <item><b>Legacy</b> (original v3, ADR-0005):
    /// <c>v3\nclientId\nuserId\nuserName\ntimestamp\nnonce</c>. This is
    /// what epr-register-enrol-backend signs on its calls to this service
    /// and what its inbound CaseManagementAuthenticationHandler verifies on
    /// our pushes back to it — so this method produces it whenever
    /// <see cref="ClientIdSignaturePayload.Role"/> and
    /// <see cref="ClientIdSignaturePayload.Nation"/> are both null.</item>
    /// <item><b>Extended</b> (RA-469):
    /// <c>v3\nclientId\nuserId\nuserName\nrole\nnation\ntimestamp\nnonce</c>.
    /// Role/nation were dropped from the payload at v3 (ADR-0005) but
    /// RA-469 puts them back: <see cref="ReAccreditationEndpoints"/>'s
    /// recycling-operations endpoint enforces AC17 authorization backend-
    /// side using the resulting user:role/user:nation claims, so those two
    /// values need the same signed-integrity guarantee as userId/userName —
    /// otherwise a party able to alter headers on an already-signed request
    /// (e.g. a misbehaving intermediary) could bypass that authorization
    /// while the signature still validates. Produced whenever either role
    /// or nation is supplied.</item>
    /// </list>
    /// The two forms cannot collide: header values cannot contain a newline,
    /// so a legacy string always has exactly six lines and an extended one
    /// exactly eight. Inbound verification accepts either form, but only
    /// when no role/nation header is present — see <see cref="VerifySignature"/>.
    /// </summary>
    internal static string ComputeSignature(
        string sharedSecret,
        ClientIdSignaturePayload payload
    ) =>
        payload.Role is null && payload.Nation is null
            ? ComputeLegacySignature(sharedSecret, payload)
            : ComputeExtendedSignature(sharedSecret, payload);

    /// <summary>
    /// The original six-line v3 form — see <see cref="ComputeSignature"/>.
    /// </summary>
    internal static string ComputeLegacySignature(
        string sharedSecret,
        ClientIdSignaturePayload payload
    ) =>
        Hmac(
            sharedSecret,
            string.Join(
                '\n',
                "v3",
                payload.ClientId,
                payload.UserId ?? string.Empty,
                payload.UserName ?? string.Empty,
                payload.Timestamp,
                payload.Nonce
            )
        );

    /// <summary>
    /// The RA-469 eight-line v3 form — see <see cref="ComputeSignature"/>.
    /// Role/nation are signed as empty strings when null; that is exactly
    /// what management-fe emits on every call, including the ones that
    /// never forward a role/nation header.
    /// </summary>
    internal static string ComputeExtendedSignature(
        string sharedSecret,
        ClientIdSignaturePayload payload
    ) =>
        Hmac(
            sharedSecret,
            string.Join(
                '\n',
                "v3",
                payload.ClientId,
                payload.UserId ?? string.Empty,
                payload.UserName ?? string.Empty,
                payload.Role ?? string.Empty,
                payload.Nation ?? string.Empty,
                payload.Timestamp,
                payload.Nonce
            )
        );

    /// <summary>
    /// Checks <paramref name="providedSignature"/> against every canonical
    /// form this request could legitimately have been signed with. When a
    /// role or nation header is present there is exactly one: the extended
    /// form over the values actually sent. When neither is present BOTH
    /// forms are acceptable — the legacy six-line form (what
    /// epr-register-enrol-backend signs) and the extended form with empty
    /// role/nation (what management-fe signs on every call, including the
    /// ones that never forward role/nation). Accepting both is safe because
    /// in that case each form binds the same semantic content and the two
    /// cannot collide (see <see cref="ComputeSignature"/>); it is NOT a
    /// downgrade path — a request signed over a real role/nation fails both
    /// candidates once those headers are stripped, and a legacy-signed
    /// request fails the only candidate once a role/nation header is added.
    /// Both candidates are always computed before the result is combined so
    /// a mismatch costs the same whichever form the caller used.
    /// </summary>
    internal static bool VerifySignature(
        string sharedSecret,
        ClientIdSignaturePayload payload,
        string providedSignature
    )
    {
        if (payload.Role is not null || payload.Nation is not null)
        {
            return FixedTimeEquals(
                providedSignature,
                ComputeExtendedSignature(sharedSecret, payload)
            );
        }

        var matchesLegacy = FixedTimeEquals(
            providedSignature,
            ComputeLegacySignature(sharedSecret, payload)
        );
        var matchesExtended = FixedTimeEquals(
            providedSignature,
            ComputeExtendedSignature(sharedSecret, payload)
        );
        return matchesLegacy || matchesExtended;
    }

    private static string Hmac(string sharedSecret, string canonicalPayload)
    {
        var keyBytes = Encoding.UTF8.GetBytes(sharedSecret);
        var payloadBytes = Encoding.UTF8.GetBytes(canonicalPayload);
        var mac = HMACSHA256.HashData(keyBytes, payloadBytes);
        return Convert.ToBase64String(mac);
    }

    // Length-independent constant-time equality. We SHA-256 both inputs to a
    // fixed 32-byte digest, then compare the digests. This avoids the
    // length-based short-circuit in CryptographicOperations.FixedTimeEquals
    // (which returns immediately when buffer lengths differ) and is therefore
    // safe for any attacker-controlled input length. Practical leak from the
    // previous form was nil (HMAC-SHA256 base64 is always 44 chars), but the
    // helper is now defensible against future callers that wire it to inputs
    // of variable length.
    private static bool FixedTimeEquals(string a, string b)
    {
        Span<byte> aHash = stackalloc byte[32];
        Span<byte> bHash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(a), aHash);
        SHA256.HashData(Encoding.UTF8.GetBytes(b), bHash);
        return CryptographicOperations.FixedTimeEquals(aHash, bHash);
    }
}

/// <summary>
/// The v3 canonical payload's identity/timing fields, bundled into one
/// argument so <see cref="ClientIdAuthenticationHandler.ComputeSignature"/>
/// stays under SonarCloud's parameter-count cap (S107) now that RA-469's
/// role/nation join userId/userName. Constructor-parameter order here is
/// irrelevant to the wire format — <see cref="ClientIdAuthenticationHandler.ComputeSignature"/>
/// reads named properties, not positional order; the WIRE order is fixed
/// inside that method's own payload string. Role/nation default to null so
/// every caller that has no use for them (e.g. <see cref="OperatorBackendSigning.AddHeaders"/>)
/// can omit them — and null (as opposed to empty) is also what selects the
/// legacy six-line wire form, see <see cref="ClientIdAuthenticationHandler.ComputeSignature"/>.
/// Declared top-level (not nested) so every caller in this namespace can
/// reference it unqualified.
/// </summary>
internal sealed record ClientIdSignaturePayload(
    string ClientId,
    string? UserId,
    string? UserName,
    string Timestamp,
    string Nonce,
    string? Role = null,
    string? Nation = null
);
