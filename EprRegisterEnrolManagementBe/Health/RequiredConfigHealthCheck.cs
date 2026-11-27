using Microsoft.Extensions.Logging;
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
public sealed class RequiredConfigHealthCheck(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<RequiredConfigHealthCheck> logger
) : IHealthCheck
{
    // Caller secrets required outside Development, as (config key, env var).
    // Missing one makes readiness Unhealthy: without it the caseworker
    // service cannot serve its own callers.
    private static readonly (string ConfigKey, string EnvVar)[] s_callerSecrets =
    [
        ("AUTH_SHARED_SECRET:MANAGEMENT_FE", "AUTH_SHARED_SECRET__MANAGEMENT_FE"),
        ("AUTH_SHARED_SECRET:BACKEND", "AUTH_SHARED_SECRET__BACKEND"),
    ];

    // RA-751 / ADR-0010: the read-only Admin UI's secret. Missing it makes
    // readiness Degraded, never Unhealthy (Degraded still answers 200), so an
    // environment without the support-tool secret is never cut off from its
    // caseworker traffic. It is logged at Error so it is still noticed.
    internal const string AdminUiSecretKey = "AUTH_SHARED_SECRET:ADMIN_UI";
    internal const string AdminUiSecretEnvVar = "AUTH_SHARED_SECRET__ADMIN_UI";

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

        if (missing.Count > 0)
        {
            return Task.FromResult(
                HealthCheckResult.Unhealthy($"Missing required config: {string.Join(", ", missing)}")
            );
        }

        if (
            !environment.IsDevelopment()
            && string.IsNullOrWhiteSpace(configuration.GetValue<string>(AdminUiSecretKey))
        )
        {
            logger.LogError(
                "{EnvVar} is not set: the read-only Admin UI cannot authenticate to this service. Caseworker traffic is unaffected.",
                AdminUiSecretEnvVar
            );
            return Task.FromResult(
                HealthCheckResult.Degraded($"Missing optional config: {AdminUiSecretEnvVar}")
            );
        }

        return Task.FromResult(HealthCheckResult.Healthy());
    }
}
