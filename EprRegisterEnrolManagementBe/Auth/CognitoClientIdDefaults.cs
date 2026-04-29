namespace EprRegisterEnrolManagementBe.Auth;

public static class CognitoClientIdDefaults
{
    public const string AuthenticationScheme = "CognitoClientId";

    /// <summary>
    /// Header carrying the calling service's CDP Cognito client ID.
    /// CDP places this header on every service-to-service request after
    /// validating the upstream JWT, so the backend can trust its presence
    /// without re-validating the token itself.
    /// </summary>
    public const string DefaultHeaderName = "x-cdp-cognito-client-id";

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
    /// Optional header carrying the end user's role list as a comma-separated
    /// string (e.g. <c>standard,assign</c>). Each role is added as a
    /// <see cref="System.Security.Claims.ClaimTypes.Role"/> claim so endpoints
    /// can use the standard <c>User.IsInRole()</c> /
    /// <c>RequireAuthorization(...)</c> patterns.
    /// </summary>
    public const string DefaultUserRolesHeaderName = "x-cdp-user-roles";

    /// <summary>
    /// Header carrying a base64 HMAC-SHA256 signature, computed by the BFF
    /// over the canonical concatenation of the trust headers, using a
    /// shared secret. Lets the backend verify the trust headers actually
    /// originated from the BFF and were not forged by a caller that bypassed
    /// CDP ingress.
    /// </summary>
    public const string DefaultSignatureHeaderName = "x-cdp-auth-signature";
}
