using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

namespace EprRegisterEnrolManagementBe.WorkItems.InYearChanges;

/// <summary>
/// RA-769: the stable accreditation an end-to-end test organisation (<c>e2e-</c>, ADR-0009) shows on the
/// Accreditation summary tab, so a case posted directly to the Case Management service can be tested without
/// operator data. Only ever served when <c>TestOrganisations__Enabled</c> is on, which startup refuses in prod
/// (<see cref="Config.TestOrganisations.EnsureNotEnabledInProd"/>), so caseworkers only ever see real data.
/// An exporter, so the tab's exporter-only rows have something to show as later tasks add them.
/// </summary>
internal static class TestOrganisationAccreditationSummary
{
    public static OperatorAccreditationSummary Fixture { get; } =
        new(
            OrganisationName: "E2E Test Organisation Ltd",
            OrganisationNumber: 500999,
            AccreditationNumber: "A26ER9999999999PL",
            RegistrationNumber: "R26ER9999999999PL",
            AccreditationYear: 2026,
            WasteProcessingType: "exporter",
            Material: "plastic",
            GlassRecyclingProcess: null,
            SiteAddress: "1 Test Street, Testville, TE1 1ST",
            CompanyRegisteredAddress: "2 Registered Road, Testville, TE2 2ST"
        );
}
