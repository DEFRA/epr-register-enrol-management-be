using Microsoft.AspNetCore.Authentication;

namespace Backend.Api.Auth;

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
}
