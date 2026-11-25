using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolManagementBe.Auth;

/// <summary>
/// RA-751 / ADR-0010: authorization for read-only callers (the Admin UI).
///
/// Every endpoint that calls <c>RequireAuthorization()</c> with no policy uses
/// the default policy, which <see cref="AddCallerAccessPolicies"/> sets to
/// "authenticated AND not a read-only caller". So a read-only caller is
/// refused (403) everywhere by default, including every write route, and a
/// new endpoint stays closed to it unless it opts in.
///
/// An endpoint opts in with <c>RequireAuthorization(CallerAccessPolicies.ReadOnlyCallerAllowed)</c>.
/// That policy lets every other caller through unchanged and lets a read-only
/// caller through only for GET or HEAD.
/// </summary>
public static class CallerAccessPolicies
{
    /// <summary>
    /// Policy for read endpoints the Admin UI may call. Full-access callers
    /// pass as they would the default policy; a read-only caller passes only
    /// for GET or HEAD.
    /// </summary>
    public const string ReadOnlyCallerAllowed = "ReadOnlyCallerAllowed";

    /// <summary>
    /// Claim added by <see cref="ClientIdAuthenticationHandler"/> to a caller
    /// listed in <see cref="ClientIdAuthenticationOptions.ReadOnlyClientIds"/>.
    /// </summary>
    public const string AccessClaimType = "client:access";

    /// <summary>The only value of <see cref="AccessClaimType"/>.</summary>
    public const string ReadOnlyAccess = "read-only";

    /// <summary>Config key overriding the Admin UI's clientId (env var <c>Auth__AdminUiClientId</c>).</summary>
    public const string AdminUiClientIdKey = "Auth:AdminUiClientId";

    /// <summary>The clientId the Admin UI asserts by default.</summary>
    public const string AdminUiClientIdDefault = "epr-register-enrol-admin";

    public static void AddCallerAccessPolicies(this AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.DefaultPolicy = new AuthorizationPolicyBuilder(ClientIdDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .AddRequirements(new FullAccessCallerRequirement())
            .Build();

        options.AddPolicy(
            ReadOnlyCallerAllowed,
            policy =>
                policy
                    .AddAuthenticationSchemes(ClientIdDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser()
                    .AddRequirements(new ReadOnlyCallerMethodRequirement())
        );
    }

    internal static bool IsReadOnlyCaller(ClaimsPrincipal user) =>
        user.HasClaim(AccessClaimType, ReadOnlyAccess);
}

/// <summary>Refuses read-only callers outright (the default policy).</summary>
public sealed class FullAccessCallerRequirement : IAuthorizationRequirement;

/// <summary>Refuses a read-only caller unless the request is GET or HEAD.</summary>
public sealed class ReadOnlyCallerMethodRequirement : IAuthorizationRequirement;

// Marker type for the logger category.
internal sealed class CallerAccessAuthorizationHandlerLogger;

/// <summary>
/// Handles both requirements. Every refusal of a read-only caller is logged at
/// Error with the caller, user, method and path: a read-only client asking for
/// anything it may not have is either a defect in that client or a stolen
/// credential, and both need to be noticed.
/// </summary>
internal sealed class CallerAccessAuthorizationHandler(
    ILogger<CallerAccessAuthorizationHandlerLogger> logger
) : IAuthorizationHandler
{
    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        var readOnly = CallerAccessPolicies.IsReadOnlyCaller(context.User);
        var http = context.Resource as HttpContext;

        foreach (var requirement in context.PendingRequirements.ToList())
        {
            switch (requirement)
            {
                case FullAccessCallerRequirement when !readOnly:
                    context.Succeed(requirement);
                    break;
                case ReadOnlyCallerMethodRequirement when !readOnly:
                    context.Succeed(requirement);
                    break;
                case ReadOnlyCallerMethodRequirement when IsReadMethod(http):
                    LogAccess(context.User, http);
                    context.Succeed(requirement);
                    break;
                case FullAccessCallerRequirement:
                case ReadOnlyCallerMethodRequirement:
                    LogRefusal(context.User, http);
                    context.Fail(new AuthorizationFailureReason(this, "Read-only caller"));
                    break;
            }
        }

        return Task.CompletedTask;
    }

    private static bool IsReadMethod(HttpContext? http) =>
        http is not null
        && (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method));

    // RA-751: the epic asks that upstream logs show the calling client and
    // the signed-in user for every Admin UI request.
    private void LogAccess(ClaimsPrincipal user, HttpContext? http) =>
        logger.LogInformation(
            "Read-only caller {ClientId} (user {UserId}) allowed: {Method} {Path}.",
            user.FindFirstValue("client_id") ?? "(absent)",
            user.FindFirstValue("user:id") ?? "(absent)",
            http?.Request.Method ?? "(unknown)",
            http?.Request.Path.Value ?? "(unknown)"
        );

    private void LogRefusal(ClaimsPrincipal user, HttpContext? http) =>
        logger.LogError(
            "Read-only caller {ClientId} (user {UserId}) refused: {Method} {Path} is not open to read-only callers.",
            user.FindFirstValue("client_id") ?? "(absent)",
            user.FindFirstValue("user:id") ?? "(absent)",
            http?.Request.Method ?? "(unknown)",
            http?.Request.Path.Value ?? "(unknown)"
        );
}
