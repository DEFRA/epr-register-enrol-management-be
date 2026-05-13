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
    /// <remarks>
    /// SECURITY: opting in via <c>Swagger:Enabled</c> in any environment
    /// also exposes the dev-only stub-user picker
    /// (<see cref="SwaggerUiStubUserAssets"/>), which auto-attaches the
    /// CDP trust headers <c>x-cdp-cognito-client-id=local-swagger-ui</c>
    /// and arbitrary <c>x-cdp-user-*</c> values to every "Try it out"
    /// request. This is only safe as long as <c>local-swagger-ui</c> is
    /// not an allow-listed Cognito client id in the target environment.
    /// Do NOT enable this flag in any environment whose data you are not
    /// happy for an authenticated operator to act on as any user/role.
    /// </remarks>
    internal static bool ShouldEnableSwaggerUi(IHostEnvironment env, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(config);

        return !env.IsProduction() || config.GetValue<bool>("Swagger:Enabled");
    }
}
