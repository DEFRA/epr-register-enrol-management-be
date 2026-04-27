using Microsoft.AspNetCore.Authentication;

namespace Backend.Api.Auth;

public class CognitoClientIdAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>
    /// Name of the request header carrying the Cognito client ID.
    /// </summary>
    public string HeaderName { get; set; } = CognitoClientIdDefaults.DefaultHeaderName;
}
