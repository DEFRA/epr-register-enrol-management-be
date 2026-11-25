using System.Globalization;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

namespace EprRegisterEnrolManagementBe.WorkItems.InYearChanges;

/// <summary>
/// RA-769: the stable accreditation an end-to-end test organisation (<c>e2e-</c>, ADR-0009) shows on the
/// Accreditation summary tab, so a case posted directly to the Case Management service can be tested without
/// operator data. Only ever served by <see cref="TestOrganisationAccreditationSummaryAdapter"/>, which is registered
/// only when <c>TestOrganisations__Enabled</c> is on, which startup refuses in prod
/// (<see cref="Config.TestOrganisations.EnsureNotEnabledInProd"/>), so caseworkers only ever see real data.
/// An exporter, so the tab's exporter-only rows have something to show.
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
            // A clean plan, and one that has not passed its scan (so is offered no download).
            SamplingPlan: new OperatorSummaryFiles(
                [
                    File("e2e-sampling-plan-clean", "sampling-and-inspection-plan.pdf", "sampling-plans", "Clean"),
                    File("e2e-sampling-plan-pending", "sampling-plan-awaiting-scan.pdf", "sampling-plans", "Pending"),
                ]
            ),
            // Four sites in ORS id order. Two share a name, so a summary that hid its address for a unique name would
            // still show it here; the last has no contact or codes, as an ORS registered with little detail does.
            // The first has one interim site, the second three, the others none. The first has one BES file, the second a clean
            // one and one awaiting its scan, the others none.
            OverseasSites:
            [
                Site(
                    1,
                    "Example Recycling GmbH",
                    ("1 Hafenstrasse", "Bremen", "Germany"),
                    isEu: true,
                    isOecd: true,
                    interimSites: [Interim(101, "I-001", "Bremen Interim Works", "R12")],
                    besFiles: [File("e2e-bes-001", "bes-example-bremen.pdf", "bes", "Clean")]
                ),
                Site(
                    2,
                    "Example Recycling GmbH",
                    ("2 Hafenstrasse", "Hamburg", "Germany"),
                    isEu: true,
                    isOecd: true,
                    interimSites:
                    [
                        Interim(201, "I-002", "Hamburg Interim North", "R12"),
                        Interim(202, "I-003", "Hamburg Interim South", "R13"),
                        Interim(203, "I-004", "Hamburg Interim East", "R12", "R3"),
                    ],
                    besFiles:
                    [
                        File("e2e-bes-002", "bes-example-hamburg.pdf", "bes", "Clean"),
                        File("e2e-bes-002-pending", "bes-example-hamburg-awaiting-scan.pdf", "bes", "Pending"),
                    ]
                ),
                Site(3, "Sample Recycling BV", ("3 Havenweg", "Rotterdam", "Netherlands"), isEu: true, isOecd: true),
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
                    IsOecd: true,
                    InterimSites: [],
                    BesEvidence: new OperatorSummaryFiles([])
                ),
            ]
        );

    // The ORS id is the site id, zero padded to three digits ("001").
    private static OperatorSummarySite Site(
        int siteId,
        string name,
        (string AddressLine1, string TownOrCity, string Country) address,
        bool isEu,
        bool isOecd,
        IReadOnlyList<OperatorSummaryInterimSite>? interimSites = null,
        IReadOnlyList<OperatorSummaryFile>? besFiles = null
    )
    {
        var orsId = siteId.ToString("D3", CultureInfo.InvariantCulture);
        return new(
            SiteId: siteId,
            OrsId: orsId,
            SiteName: name,
            SiteAddress: null,
            AddressLine1: address.AddressLine1,
            AddressLine2: null,
            TownOrCity: address.TownOrCity,
            Country: address.Country,
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
            IsOecd: isOecd,
            InterimSites: interimSites ?? [],
            BesEvidence: new OperatorSummaryFiles(besFiles ?? [])
        );
    }

    // The S3 location is made up: nothing is stored there, so a download of a clean stub file is an S3 failure.
    private static OperatorSummaryFile File(string fileId, string filename, string folder, string scanStatus) =>
        new(
            FileId: fileId,
            Filename: filename,
            ContentType: "application/pdf",
            ScanStatus: scanStatus,
            S3Key: $"e2e-stub/{folder}/{fileId}.pdf",
            S3Bucket: "e2e-stub-bucket"
        );

    private static OperatorSummaryInterimSite Interim(
        int siteId,
        string siteNumber,
        string name,
        params string[] operationCodes
    ) =>
        new(
            SiteId: siteId,
            SiteNumber: siteNumber,
            SiteName: name,
            AddressLine1: $"{siteId} Interim Strasse",
            AddressLine2: null,
            TownOrCity: "Hamburg",
            StateOrRegion: null,
            Postcode: "20095",
            Country: "Germany",
            ContactName: "Interim Contact " + siteNumber,
            ContactEmail: $"interim.{siteNumber}@example.test".ToLowerInvariant(),
            ContactPhone: "+49 40 1234 5678",
            OperationCodes: operationCodes
        );
}
