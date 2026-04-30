using Microsoft.AspNetCore.Authentication;

namespace EprRegisterEnrolManagementBe.Auth;

public class CognitoClientIdAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>
    /// Name of the request header carrying the Cognito client ID.
    /// </summary>
    public string HeaderName { get; set; } = CognitoClientIdDefaults.DefaultHeaderName;

    /// <summary>Optional header carrying the end user's identifier.</summary>
    public string UserIdHeaderName { get; set; } = CognitoClientIdDefaults.DefaultUserIdHeaderName;

    /// <summary>Optional header carrying the end user's display name.</summary>
    public string UserNameHeaderName { get; set; } = CognitoClientIdDefaults.DefaultUserNameHeaderName;

    /// <summary>Optional header carrying the end user's roles as a comma-separated string.</summary>
    public string UserRolesHeaderName { get; set; } = CognitoClientIdDefaults.DefaultUserRolesHeaderName;

    /// <summary>
    /// Name of the request header carrying the BFF-computed HMAC signature
    /// over the trust headers. Used to prove the headers originated from a
    /// caller that holds the configured <see cref="SharedSecret"/>; defends
    /// against direct backend access bypassing the BFF.
    /// </summary>
    public string SignatureHeaderName { get; set; } = CognitoClientIdDefaults.DefaultSignatureHeaderName;

    /// <summary>
    /// Header carrying the BFF's ISO-8601 UTC instant for the request. Part
    /// of the v2 canonical signing payload — bounds replay windows.
    /// </summary>
    public string TimestampHeaderName { get; set; } = CognitoClientIdDefaults.DefaultTimestampHeaderName;

    /// <summary>
    /// Header carrying a per-request opaque nonce minted by the BFF (e.g.
    /// base64url of 16 random bytes). Part of the v2 canonical signing
    /// payload — single-use within the replay cache TTL.
    /// </summary>
    public string NonceHeaderName { get; set; } = CognitoClientIdDefaults.DefaultNonceHeaderName;

    /// <summary>
    /// Shared secret used to validate the <see cref="SignatureHeaderName"/>
    /// HMAC. When set, every authenticated request MUST present a valid
    /// signature or the handler fails closed. When null/empty the handler
    /// falls back to header-trust mode (development/local only).
    /// </summary>
    public string? SharedSecret { get; set; }

    /// <summary>
    /// Maximum permitted absolute difference between the BFF-supplied
    /// timestamp header and the backend's clock. Enforced in both
    /// directions to bound replay windows even if a request is captured
    /// before the BFF clock advances. Defaults to 5 minutes.
    /// </summary>
    public TimeSpan MaxClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Lifetime of an entry in the in-memory nonce replay cache. Should be
    /// at least <c>2 * MaxClockSkew</c> so a request that arrived at the
    /// edge of the freshness window cannot be replayed by re-using a
    /// nonce that has already aged out of the cache. Defaults to 10
    /// minutes.
    /// </summary>
    public TimeSpan ReplayCacheTtl { get; set; } = TimeSpan.FromMinutes(10);
}
