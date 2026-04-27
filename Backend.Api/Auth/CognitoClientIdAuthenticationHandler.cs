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

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, clientId),
            new("cognito:client_id", clientId)
        };

        // The BFF (frontend) forwards the acting user's identity and role
        // membership in optional headers. They are not authenticators in
        // their own right — CDP has already validated the upstream JWT and
        // placed the trusted client id in the primary header — but they let
        // backend endpoints make role-based decisions and produce more
        // useful audit log lines without a separate user lookup.
        if (Request.Headers.TryGetValue(Options.UserIdHeaderName, out var userIdValues))
        {
            var userId = userIdValues.ToString();
            if (!string.IsNullOrWhiteSpace(userId))
            {
                claims.Add(new Claim("user:id", userId));
            }
        }

        if (Request.Headers.TryGetValue(Options.UserNameHeaderName, out var userNameValues))
        {
            var userName = userNameValues.ToString();
            if (!string.IsNullOrWhiteSpace(userName))
            {
                claims.Add(new Claim("user:name", userName));
            }
        }

        if (Request.Headers.TryGetValue(Options.UserRolesHeaderName, out var rolesValues))
        {
            var rolesHeader = rolesValues.ToString();
            if (!string.IsNullOrWhiteSpace(rolesHeader))
            {
                foreach (var role in rolesHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    claims.Add(new Claim(ClaimTypes.Role, role));
                }
            }
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
