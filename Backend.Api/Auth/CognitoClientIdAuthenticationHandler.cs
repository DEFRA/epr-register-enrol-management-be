using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backend.Api.Auth;

/// <summary>
/// Authentication handler that accepts a CDP Cognito client ID supplied in a
/// request header. CDP validates the upstream service's JWT before forwarding
/// the request, so the presence of the header is sufficient — no further
/// authorisation is performed.
/// </summary>
public class CognitoClientIdAuthenticationHandler(
    IOptionsMonitor<CognitoClientIdAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<CognitoClientIdAuthenticationOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headerName = Options.HeaderName;

        if (!Request.Headers.TryGetValue(headerName, out var values))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var clientId = values.ToString();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return Task.FromResult(AuthenticateResult.Fail($"Empty {headerName} header"));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, clientId),
            new Claim("cognito:client_id", clientId)
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
