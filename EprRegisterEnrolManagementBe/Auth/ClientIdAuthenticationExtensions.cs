using Microsoft.AspNetCore.Authentication;

namespace EprRegisterEnrolManagementBe.Auth;

public static class ClientIdAuthenticationExtensions
{
    public static AuthenticationBuilder AddClientId(
        this AuthenticationBuilder builder,
        Action<ClientIdAuthenticationOptions>? configure = null)
    {
        return builder.AddScheme<ClientIdAuthenticationOptions, ClientIdAuthenticationHandler>(
            ClientIdDefaults.AuthenticationScheme,
            displayName: "CDP Client ID",
            configureOptions: configure ?? (_ => { }));
    }
}