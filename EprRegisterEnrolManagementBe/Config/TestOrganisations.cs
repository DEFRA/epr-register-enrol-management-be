namespace EprRegisterEnrolManagementBe.Config;

/// <summary>
/// RA-000: end-to-end test organisations. The mgmt-tests journey suite runs
/// against CDP <c>test</c>, whose Mongo is never reset and whose upstreams
/// (ReEx, the operator journey backend) are real, so it provisions its own
/// uniquely named data under organisation ids starting with
/// <see cref="OrganisationIdPrefix"/>.
///
/// <para>
/// <c>TestOrganisations__Enabled</c> (default <c>false</c>) turns on, and is
/// the only thing that turns on:
/// <list type="bullet">
/// <item>routing ReEx lookups for <c>e2e-</c> organisations to
/// <c>StubReExAccreditationClient</c> (see
/// <c>TestOrganisationReExAccreditationClient</c>) — real ReEx has never
/// heard of them;</item>
/// <item>the <c>epr-register-enrol-mgmt-tests</c> HMAC caller
/// (<c>AUTH_SHARED_SECRET__MGMT_TESTS</c>), so the suite can create work
/// items directly.</item>
/// </list>
/// The service refuses to start with it on in CDP <c>prod</c> — see
/// <see cref="EnsureNotEnabledInProd"/>.
/// </para>
/// </summary>
public static class TestOrganisations
{
    /// <summary>Config key of the switch (env var <c>TestOrganisations__Enabled</c>).</summary>
    public const string EnabledKey = "TestOrganisations:Enabled";

    /// <summary>
    /// Lower-case, case-sensitive prefix marking an organisation id as an
    /// end-to-end test organisation. Matches the operator journey backend.
    /// </summary>
    public const string OrganisationIdPrefix = "e2e-";

    /// <summary>Whether <c>TestOrganisations__Enabled</c> is true.</summary>
    public static bool IsEnabled(IConfiguration configuration) =>
        configuration.GetValue<bool>(EnabledKey);

    /// <summary>
    /// True when <paramref name="organisationId"/> starts with
    /// <see cref="OrganisationIdPrefix"/> (ordinal, so <c>E2E-</c> is not a
    /// test organisation).
    /// </summary>
    public static bool IsTestOrganisation(string? organisationId) =>
        organisationId is not null
        && organisationId.StartsWith(OrganisationIdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Fails startup when test organisations are enabled in CDP
    /// <c>prod</c>: production must never route an organisation to stub ReEx
    /// data or accept the mgmt-tests caller. A config mistake, never a
    /// legitimate state, so it throws rather than logging.
    /// </summary>
    public static void EnsureNotEnabledInProd(IConfiguration configuration, string? cdpEnvironment)
    {
        if (IsEnabled(configuration) && CdpEnvironment.IsProd(cdpEnvironment))
        {
            throw new InvalidOperationException(
                "TestOrganisations__Enabled is true but the CDP environment is "
                    + $"'{cdpEnvironment}'. End-to-end test organisations and the mgmt-tests "
                    + "caller must never be enabled in production; remove the setting."
            );
        }
    }
}
