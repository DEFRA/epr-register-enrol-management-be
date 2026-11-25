using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace EprRegisterEnrolManagementBe.Health;

/// <summary>
/// Readiness check for required config that otherwise has no signal at all when
/// missing. <c>AddCallerSecret</c> (Program.cs) treats a blank per-caller shared
/// secret as "caller not configured yet" and silently no-ops, and
/// <c>ConfigureReEx</c> silently registers <c>StubReExAccreditationClient</c> instead
/// of the real HTTP client when <c>REEX_API_BASIC_AUTH_USERNAME</c> is blank — both
/// intentional for local development (see appsettings.Development.json, which never
/// sets any of these), but dangerous left unset in a deployed environment where
/// nothing else reports it. Mirrors <c>RequiredConfigHealthCheck</c> in the sibling
/// epr-register-enrol-backend repo (RA-441).
/// </summary>
public sealed class RequiredConfigHealthCheck(IConfiguration configuration, IHostEnvironment environment)
    : IHealthCheck
{
    // Caller secrets required outside Development, as (config key, env var).
    // RA-751 / ADR-0010 adds the Admin UI's: reported here, not thrown at
    // startup, so a missing support-tool secret cannot stop the caseworker
    // service starting.
    private static readonly (string ConfigKey, string EnvVar)[] s_callerSecrets =
    [
        ("AUTH_SHARED_SECRET:MANAGEMENT_FE", "AUTH_SHARED_SECRET__MANAGEMENT_FE"),
        ("AUTH_SHARED_SECRET:BACKEND", "AUTH_SHARED_SECRET__BACKEND"),
        ("AUTH_SHARED_SECRET:ADMIN_UI", "AUTH_SHARED_SECRET__ADMIN_UI"),
    ];

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var missing = new List<string>();

        // docs/cdp-deployment.md: both caller secrets are "Required in all
        // non-Development environments." AddCallerSecret has no other signal when
        // one is blank — the caller just never gets an entry in ClientSecrets.
        if (!environment.IsDevelopment())
        {
            missing.AddRange(
                s_callerSecrets
                    .Where(secret => string.IsNullOrWhiteSpace(configuration.GetValue<string>(secret.ConfigKey)))
                    .Select(secret => secret.EnvVar)
            );
        }

        // A blank username is the expected, silent default in Development. Anywhere
        // else it means the stub client is quietly running with zero signal, so flag
        // it there. Once a username IS set (the real client is active, in any
        // environment), the URL and password become load-bearing and are checked
        // unconditionally.
        var reExUsername = configuration.GetValue<string>("REEX_API_BASIC_AUTH_USERNAME");
        if (string.IsNullOrWhiteSpace(reExUsername))
        {
            if (!environment.IsDevelopment())
                missing.Add(
                    "REEX_API_BASIC_AUTH_USERNAME (stub ReEx client still running outside Development)"
                );
        }
        else
        {
            if (string.IsNullOrWhiteSpace(configuration.GetValue<string>("ReExApi:BaseUrl")))
                missing.Add("ReExApi__BaseUrl");
            if (
                string.IsNullOrWhiteSpace(
                    configuration.GetValue<string>("REEX_API_BASIC_AUTH_PASSWORD")
                )
            )
                missing.Add("REEX_API_BASIC_AUTH_PASSWORD");
        }

        return Task.FromResult(
            missing.Count == 0
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"Missing required config: {string.Join(", ", missing)}")
        );
    }
}
