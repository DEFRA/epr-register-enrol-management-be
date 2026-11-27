using System.Security.Claims;

namespace EprRegisterEnrolManagementBe.Auth;

/// <summary>
/// RA-751: the calling client and signed-in user of a request, as read from
/// the claims <see cref="ClientIdAuthenticationHandler"/> sets, for log lines.
/// <see cref="AbsentValue"/> stands in for a claim the request did not carry;
/// it is a log placeholder only, never data.
/// </summary>
internal sealed record CallerIdentity(string ClientId, string UserId)
{
    internal const string AbsentValue = "(absent)";

    internal static CallerIdentity From(ClaimsPrincipal user) =>
        new(
            user.FindFirstValue("client_id") ?? AbsentValue,
            user.FindFirstValue("user:id") ?? AbsentValue
        );
}
