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
    /// Shared secret used to validate the <see cref="SignatureHeaderName"/>
    /// HMAC. When set, every authenticated request MUST present a valid
    /// signature or the handler fails closed. When null/empty the handler
    /// falls back to header-trust mode (development/local only).
    /// </summary>
    public string? SharedSecret { get; set; }
}
