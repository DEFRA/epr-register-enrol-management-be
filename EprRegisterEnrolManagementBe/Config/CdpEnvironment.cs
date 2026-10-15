namespace EprRegisterEnrolManagementBe.Config;

/// <summary>
/// The CDP platform environment name (<c>local</c>, <c>dev</c>, <c>test</c>,
/// <c>perf-test</c>, <c>ext-test</c>, <c>prod</c>, …) that CDP injects as the
/// <c>ENVIRONMENT</c> variable. Same variable the frontends bind their
/// <c>environment</c> config to. Read by <c>NotifySendingPolicy</c> (whether
/// email is really dispatched) and <see cref="TestOrganisations"/> (refusing
/// to start with test organisations enabled in prod).
/// </summary>
public static class CdpEnvironment
{
    /// <summary>Name of the process environment variable CDP sets.</summary>
    public const string Variable = "ENVIRONMENT";

    /// <summary>The CDP environment name of production.</summary>
    public const string Prod = "prod";

    /// <summary>
    /// Read the CDP environment name from the process environment.
    ///
    /// Deliberately NOT read through <see cref="IConfiguration"/>:
    /// <c>ENVIRONMENT</c> is also <c>WebHostDefaults.EnvironmentKey</c>, so
    /// whenever the ASP.NET host environment is set explicitly (an
    /// <c>ASPNETCORE_ENVIRONMENT</c> value, or <c>UseEnvironment</c> in a
    /// <c>WebApplicationFactory</c> test) host configuration shadows the key
    /// and <c>configuration["ENVIRONMENT"]</c> hands back the host
    /// environment name — "Development", not "dev" — instead of the platform
    /// value. Going straight to the variable keeps the two inputs separate.
    /// </summary>
    public static string? Read() => Environment.GetEnvironmentVariable(Variable);

    /// <summary>
    /// True when <paramref name="cdpEnvironment"/> names production
    /// (case-insensitive, surrounding whitespace ignored).
    /// </summary>
    public static bool IsProd(string? cdpEnvironment) =>
        string.Equals(cdpEnvironment?.Trim(), Prod, StringComparison.OrdinalIgnoreCase);
}
