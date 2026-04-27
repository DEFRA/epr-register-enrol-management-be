namespace Backend.Api.Auth;

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
}
