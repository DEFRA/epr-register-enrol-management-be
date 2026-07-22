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

    /// <summary>
    /// Name of the request header carrying the BFF-computed HMAC signature
    /// over the trust headers. Used to prove the headers originated from a
    /// caller that holds the configured <see cref="SharedSecret"/>; defends
    /// against direct backend access bypassing the BFF.
    /// </summary>
    public string SignatureHeaderName { get; set; } = CognitoClientIdDefaults.DefaultSignatureHeaderName;

    /// <summary>
    /// Header carrying the BFF's ISO-8601 UTC instant for the request. Part
    /// of the v3 canonical signing payload — bounds replay windows.
    /// </summary>
    public string TimestampHeaderName { get; set; } = CognitoClientIdDefaults.DefaultTimestampHeaderName;

    /// <summary>
    /// Header carrying a per-request opaque nonce minted by the BFF (e.g.
    /// base64url of 16 random bytes). Part of the v3 canonical signing
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

    /// <summary>
    /// Maximum permitted length (in UTF-16 chars) of the Cognito client id
    /// header. Oversize values are rejected with 401 BEFORE any further
    /// processing — avoids attacker-controlled header bytes feeding into
    /// downstream allocations or HMAC computation.
    /// </summary>
    public int MaxClientIdLength { get; set; } = 256;

    /// <summary>
    /// Maximum permitted length of the user id header. Persisted into
    /// <c>WorkItem.AuditLog.CreatedBy</c> and <c>WorkItemNote.CreatedBy</c>
    /// — capping defends Mongo document size against a misbehaving BFF or
    /// a caller that holds the shared secret.
    /// </summary>
    public int MaxUserIdLength { get; set; } = 128;

    /// <summary>
    /// Maximum permitted length of the user display name header. Persisted
    /// into <c>WorkItem.AuditLog.CreatedByName</c> and
    /// <c>WorkItemNote.CreatedByName</c>.
    /// </summary>
    public int MaxUserNameLength { get; set; } = 256;

    /// <summary>
    /// Maximum permitted length of the HMAC signature header. Base64 of
    /// SHA-256 is 44 chars; the default leaves headroom for any future
    /// algorithm bump while preventing megabyte-sized signatures from
    /// reaching the constant-time comparator.
    /// </summary>
    public int MaxSignatureLength { get; set; } = 256;

    /// <summary>
    /// Maximum permitted length of the timestamp header. ISO-8601 with
    /// offset is well under 40 chars.
    /// </summary>
    public int MaxTimestampLength { get; set; } = 64;

    /// <summary>
    /// Maximum permitted length of the nonce header.
    /// </summary>
    public int MaxNonceLength { get; set; } = 128;
}
