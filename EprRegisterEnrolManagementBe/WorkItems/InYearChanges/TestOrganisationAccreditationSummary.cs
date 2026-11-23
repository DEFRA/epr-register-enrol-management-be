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
            CompanyRegisteredAddress: "2 Registered Road, Testville, TE2 2ST",
            Prns: new OperatorSummaryPrns(
                "UpTo5000",
                [
                    new OperatorSummaryAuthoriser("Ada Authoriser", "ada.authoriser@example.test"),
                    new OperatorSummaryAuthoriser("Bob Signer", "bob.signer@example.test"),
                ]
            ),
            BusinessPlan: new OperatorSummaryBusinessPlan
            {
                NewInfrastructurePercent = 60,
                NewInfrastructureDetail = "A new sorting line",
                PriceSupportPercent = 40,
                PriceSupportDetail = "Support for collection prices",
            },
            // Four sites in ORS id order. Two share a name, so a summary that hid its address for a unique name would
            // still show it here; the last has no contact or codes, as an ORS registered with little detail does.
            OverseasSites:
            [
                Site(1, "001", "Speira GmbH", "1 Hafenstrasse", "Bremen", "Germany", isEu: true, isOecd: true),
                Site(2, "002", "Speira GmbH", "2 Hafenstrasse", "Hamburg", "Germany", isEu: true, isOecd: true),
                Site(3, "003", "Excel Recycling BV", "3 Havenweg", "Rotterdam", "Netherlands", isEu: true, isOecd: true),
                new OperatorSummarySite(
                    SiteId: 4,
                    OrsId: "004",
                    SiteName: "Fox Reprocessing Inc",
                    SiteAddress: null,
                    AddressLine1: "4 Harbour Road",
                    AddressLine2: null,
                    TownOrCity: "Halifax",
                    Country: "Canada",
                    Coordinates: null,
                    ContactName: null,
                    ContactEmail: null,
                    ContactPhone: null,
                    OperationCodes: [],
                    Code1: null,
                    Code2: null,
                    Code3: null,
                    RepatriatedLoads: null,
                    ConditionsOfExport: false,
                    IsEu: false,
                    IsOecd: true
                ),
            ]
        );

    private static OperatorSummarySite Site(
        int siteId,
        string orsId,
        string name,
        string addressLine1,
        string townOrCity,
        string country,
        bool isEu,
        bool isOecd
    ) =>
        new(
            SiteId: siteId,
            OrsId: orsId,
            SiteName: name,
            SiteAddress: null,
            AddressLine1: addressLine1,
            AddressLine2: null,
            TownOrCity: townOrCity,
            Country: country,
            Coordinates: "51.847,7.123",
            ContactName: "Test Contact " + orsId,
            ContactEmail: $"contact.{orsId}@example.test",
            ContactPhone: "+44 20 7946 0000",
            OperationCodes: ["R4"],
            Code1: "B1010",
            Code2: "B1020",
            Code3: null,
            RepatriatedLoads: "As per article 18",
            ConditionsOfExport: true,
            IsEu: isEu,
            IsOecd: isOecd
        );
}
