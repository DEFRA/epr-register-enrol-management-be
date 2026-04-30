using Microsoft.AspNetCore.Authentication;

namespace EprRegisterEnrolManagementBe.Auth;

public static class CognitoClientIdAuthenticationExtensions
{
    public static AuthenticationBuilder AddCognitoClientId(
        this AuthenticationBuilder builder,
        Action<CognitoClientIdAuthenticationOptions>? configure = null)
    {
        return builder.AddScheme<CognitoClientIdAuthenticationOptions, CognitoClientIdAuthenticationHandler>(
            CognitoClientIdDefaults.AuthenticationScheme,
            displayName: "CDP Cognito Client ID",
            configureOptions: configure ?? (_ => { }));
    }
}