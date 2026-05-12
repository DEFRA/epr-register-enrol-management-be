namespace EprRegisterEnrolManagementBe.Config;

/// <summary>
/// Gating logic for whether the Swagger UI explorer should be wired up.
/// Extracted from Program.cs so it can be unit-tested without booting the
/// full host. See RA-124.
/// </summary>
internal static class SwaggerUiGating
{
    /// <summary>
    /// Swagger UI is enabled when the host is NOT Production, OR when the
    /// explicit <c>Swagger:Enabled</c> configuration flag is set to
    /// <c>true</c>. The flag exists so a CDP environment that needs the
    /// explorer for diagnostics can opt in without redeploying with a
    /// non-Production environment name.
    /// </summary>
    internal static bool ShouldEnableSwaggerUi(IHostEnvironment env, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(config);

        return !env.IsProduction() || config.GetValue<bool>("Swagger:Enabled");
    }
}
