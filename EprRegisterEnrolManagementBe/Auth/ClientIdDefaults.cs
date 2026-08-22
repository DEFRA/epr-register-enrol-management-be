namespace EprRegisterEnrolManagementBe.Auth;

public static class ClientIdDefaults
{
    public const string AuthenticationScheme = "ClientId";

    /// <summary>
    /// Header carrying the calling service's self-asserted client ID.
    /// Not verified by CDP itself — trust comes from the HMAC signature
    /// (see <see cref="DefaultSignatureHeaderName"/>), not from this header
    /// alone.
    /// </summary>
    public const string DefaultHeaderName = "x-cdp-client-id";

    /// <summary>
    /// Optional header carrying the end user's identifier as the BFF sees it
    /// (typically the session user's <c>id</c>). Lets the backend distinguish
    /// the human acting via the BFF from the BFF service identity supplied in
    /// <see cref="DefaultHeaderName"/>.
    /// </summary>
    public const string DefaultUserIdHeaderName = "x-cdp-user-id";

    /// <summary>
    /// Optional header carrying the end user's display name (used for audit
    /// log lines and as the snapshot name on assignment writes so list views
    /// can render an assignee without a separate lookup).
    /// </summary>
    public const string DefaultUserNameHeaderName = "x-cdp-user-name";

    /// <summary>
    /// Optional header carrying the caller's role as the BFF sees it (e.g.
    /// <c>"standard"</c>/<c>"support-readonly"</c>) — becomes claim
    /// <c>"user:role"</c>. RA-469 groundwork: not folded into the v3 HMAC
    /// canonical payload — see <see cref="ClientIdAuthenticationHandler.ComputeSignature"/>.
    /// </summary>
    public const string DefaultRoleHeaderName = "x-cdp-user-role";

    /// <summary>
    /// Optional header carrying the caller's nation scope as the BFF sees
    /// it (e.g. <c>"England"</c>/<c>"Scotland"</c>/<c>"Wales"</c>/
    /// <c>"NorthernIreland"</c>) — becomes claim <c>"user:nation"</c>.
    /// RA-469 groundwork: not folded into the v3 HMAC canonical payload —
    /// see <see cref="ClientIdAuthenticationHandler.ComputeSignature"/>.
    /// </summary>
    public const string DefaultNationHeaderName = "x-cdp-user-nation";

    /// <summary>
    /// Header carrying a base64 HMAC-SHA256 signature, computed by the BFF
    /// over the canonical concatenation of the trust headers, using a
    /// shared secret. Lets the backend verify the trust headers actually
    /// originated from the BFF and were not forged by a caller that bypassed
    /// CDP ingress.
    /// </summary>
    public const string DefaultSignatureHeaderName = "x-cdp-auth-signature";

    /// <summary>
    /// Header carrying the BFF's ISO-8601 UTC instant for this request.
    /// Mandatory whenever the shared secret is configured: bounds the
    /// freshness window in which a captured request may be replayed.
    /// </summary>
    public const string DefaultTimestampHeaderName = "x-cdp-auth-timestamp";

    /// <summary>
    /// Header carrying a per-request opaque nonce minted by the BFF.
    /// Mandatory whenever the shared secret is configured: tracked
    /// server-side so a captured request cannot be replayed even within
    /// the freshness window.
    /// </summary>
    public const string DefaultNonceHeaderName = "x-cdp-auth-nonce";
}
