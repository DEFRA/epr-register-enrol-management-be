using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-318: unit coverage for the deterministic, payload-derived
/// applicationReference generator. Format:
/// AP + 2-digit year + 2-char agency + last 5 chars of organisationId +
/// last 3 chars of postcode + first 2 chars of material, upper-cased,
/// capped at <see cref="ApplicationReferenceGenerator.MaxLength"/> chars
/// (this value doubles as a BACS payment reference). Attempts beyond the
/// first (the collision-retry path) replace the final character with a
/// disambiguator.
/// </summary>
public sealed class ApplicationReferenceGeneratorTests
{
    // The case-management admin UI nests the postcode under
    // payload.siteAddress.postcode (matching its Joi schema and
    // ReAccreditationNationRoutingHook.ExtractPostcode) — this fixture
    // mirrors that shape. The operator-facing backend BFF instead sends a
    // flat siteAddressPostcode key alongside a string siteAddress
    // (HttpCaseWorkingApiAdapter.BuildPayload); see MakeFlatPayload below
    // for that shape.
    private static BsonDocument MakePayload(
        object? accreditationYear = null,
        string? operatorOrganisationId = "50002",
        string? siteAddressPostcode = "SW1A 1AA",
        string? material = "Glass"
    )
    {
        var doc = new BsonDocument();
        if (accreditationYear is not null)
            doc["accreditationYear"] = BsonValue.Create(accreditationYear);
        if (operatorOrganisationId is not null)
            doc["operatorOrganisationId"] = operatorOrganisationId;
        if (siteAddressPostcode is not null)
            doc["siteAddress"] = new BsonDocument { ["postcode"] = siteAddressPostcode };
        if (material is not null)
            doc["material"] = material;
        return doc;
    }

    // Mirrors the operator-facing backend BFF's payload shape
    // (HttpCaseWorkingApiAdapter.BuildPayload): siteAddress is a plain
    // string and the postcode is a separate flat siteAddressPostcode key.
    private static BsonDocument MakeFlatPayload(
        object? accreditationYear = null,
        string? operatorOrganisationId = "50002",
        string? siteAddressPostcode = "SW1A 1AA",
        string? material = "Glass",
        string? wasteProcessingType = null,
        string? companyRegisterAddressPostcode = null
    )
    {
        var doc = new BsonDocument();
        if (accreditationYear is not null)
            doc["accreditationYear"] = BsonValue.Create(accreditationYear);
        if (operatorOrganisationId is not null)
            doc["operatorOrganisationId"] = operatorOrganisationId;
        doc["siteAddress"] = "1 Example Street, Example Town";
        if (siteAddressPostcode is not null)
            doc["siteAddressPostcode"] = siteAddressPostcode;
        if (material is not null)
            doc["material"] = material;
        if (wasteProcessingType is not null)
            doc["wasteProcessingType"] = wasteProcessingType;
        if (companyRegisterAddressPostcode is not null)
            doc["companyRegisterAddressPostcode"] = companyRegisterAddressPostcode;
        return doc;
    }

    [Fact]
    public void Generate_builds_expected_reference_for_england_postcode()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(accreditationYear: 2026);

        var reference = generator.Generate(payload);

        Assert.Equal("AP26EA500021AAGL", reference);
    }

    [Theory]
    [InlineData("EH1 1AA", "SE")] // Scotland
    [InlineData("CF10 1AA", "NR")] // Wales
    [InlineData("BT1 1AA", "NI")] // Northern Ireland
    [InlineData("SW1A 1AA", "EA")] // England
    [InlineData(null, "EA")] // missing postcode fails open to England
    public void Generate_derives_agency_code_from_postcode(string? postcode, string expectedAgency)
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(accreditationYear: 2026, siteAddressPostcode: postcode);

        var reference = generator.Generate(payload);

        Assert.Equal(expectedAgency, reference.Substring(4, 2));
    }

    [Fact]
    public void Generate_upper_cases_the_whole_reference()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(
            accreditationYear: 2026,
            siteAddressPostcode: "sw1a 1aa",
            material: "glass"
        );

        var reference = generator.Generate(payload);

        Assert.Equal(reference.ToUpperInvariant(), reference);
        Assert.Equal("AP26EA500021AAGL", reference);
    }

    [Fact]
    public void Generate_falls_back_to_current_year_when_accreditationYear_missing()
    {
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var generator = new ApplicationReferenceGenerator(fakeTime);
        var payload = MakePayload(accreditationYear: null);

        var reference = generator.Generate(payload);

        Assert.StartsWith("AP31", reference);
    }

    [Fact]
    public void Generate_caps_organisationId_to_its_last_five_characters()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(
            accreditationYear: 2026,
            operatorOrganisationId: "6a2fcd74e16883c137d01188"
        );

        var reference = generator.Generate(payload);

        // Only the last 5 chars of the raw id ("01188") appear, not the full 24-char id.
        Assert.Equal("AP26EA011881AAGL", reference);
        Assert.True(reference.Length < ApplicationReferenceGenerator.MaxLength);
    }

    [Fact]
    public void Generate_leaves_organisationId_unchanged_when_five_characters_or_fewer()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(accreditationYear: 2026, operatorOrganisationId: "1234");

        var reference = generator.Generate(payload);

        Assert.Equal("AP26EA12341AAGL", reference);
    }

    [Fact]
    public void Generate_handles_missing_organisationId_postcode_and_material_gracefully()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(
            accreditationYear: 2026,
            operatorOrganisationId: null,
            siteAddressPostcode: null,
            material: null
        );

        var reference = generator.Generate(payload);

        Assert.Equal("AP26EA", reference);
    }

    [Fact]
    public void Generate_is_deterministic_for_the_same_payload()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(accreditationYear: 2026);

        var first = generator.Generate(payload);
        var second = generator.Generate(payload);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Generate_never_exceeds_MaxLength()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(
            accreditationYear: 2026,
            operatorOrganisationId: "999999999999999999999999999999"
        );

        var reference = generator.Generate(payload);

        Assert.True(reference.Length <= ApplicationReferenceGenerator.MaxLength);
    }

    [Fact]
    public void Generate_with_attempt_greater_than_one_differs_from_the_first_attempt()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(accreditationYear: 2026);

        var first = generator.Generate(payload, attempt: 1);
        var second = generator.Generate(payload, attempt: 2);

        Assert.NotEqual(first, second);
        Assert.True(second.Length <= ApplicationReferenceGenerator.MaxLength);
    }

    [Fact]
    public void Generate_disambiguates_differently_for_each_retry_attempt()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(accreditationYear: 2026);

        var attempts = Enumerable
            .Range(2, 4) // attempts 2..5, matching WorkItemService.MaxApplicationReferenceAttempts
            .Select(attempt => generator.Generate(payload, attempt))
            .ToList();

        Assert.Equal(attempts.Count, attempts.Distinct().Count());
    }

    // No payload can push a reference to MaxLength (18) any more: with the
    // organisationId now capped to 5 chars, the longest possible reference is
    // 2 (prefix) + 2 (year) + 2 (agency) + 5 (organisationId) + 3 (postcode) +
    // 2 (material) = 16 chars. The "replace final char at MaxLength"
    // disambiguator branch in Generate is therefore unreachable via the
    // public API; Generate_disambiguator_extends_a_short_reference_rather_than_replacing_a_character
    // below covers the (now universal) short-reference disambiguator path.

    [Fact]
    public void Generate_disambiguator_extends_a_short_reference_rather_than_replacing_a_character()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(accreditationYear: 2026);

        var first = generator.Generate(payload, attempt: 1);
        var second = generator.Generate(payload, attempt: 2);

        Assert.Equal(first.Length + 1, second.Length);
        Assert.StartsWith(first, second);
    }

    [Fact]
    public void Generate_builds_expected_reference_for_the_backend_bff_flat_payload_shape()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakeFlatPayload(accreditationYear: 2026);

        var reference = generator.Generate(payload);

        Assert.Equal("AP26EA500021AAGL", reference);
    }

    [Theory]
    [InlineData("EH1 1AA", "SE")] // Scotland
    [InlineData("CF10 1AA", "NR")] // Wales
    [InlineData("BT1 1AA", "NI")] // Northern Ireland
    [InlineData("SW1A 1AA", "EA")] // England
    [InlineData(null, "EA")] // missing postcode fails open to England
    public void Generate_derives_agency_code_from_the_flat_payload_shape(
        string? postcode,
        string expectedAgency
    )
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakeFlatPayload(accreditationYear: 2026, siteAddressPostcode: postcode);

        var reference = generator.Generate(payload);

        Assert.Equal(expectedAgency, reference.Substring(4, 2));
    }

    [Fact]
    public void Generate_prefers_the_flat_postcode_key_when_both_shapes_are_present()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakeFlatPayload(accreditationYear: 2026, siteAddressPostcode: "M1 1AE");
        payload["siteAddress"] = new BsonDocument
        {
            ["line1"] = "1 Example Street",
            ["postcode"] = "BS1 1AA",
        };
        payload["siteAddressPostcode"] = "M1 1AE";

        var reference = generator.Generate(payload);

        // Last 3 chars of the flat "M1 1AE" ("1AE"), not the nested "BS1 1AA" ("1AA").
        Assert.Equal("AP26EA500021AEGL", reference);
    }

    // --- RA-314: regional regulator derived from operator type + location ---

    [Fact]
    public void AC01_exporter_reference_is_derived_from_the_registered_office_postcode()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakeFlatPayload(
            accreditationYear: 2026,
            wasteProcessingType: "exporter",
            siteAddressPostcode: "SW1A 1AA", // England site — must be ignored for an exporter
            companyRegisterAddressPostcode: "EH1 1AA" // Scotland registered office
        );

        var reference = generator.Generate(payload);

        Assert.Equal("SE", reference.Substring(4, 2));
        Assert.Equal("AP26SE500021AAGL", reference);
    }

    [Fact]
    public void AC02_reprocessor_reference_is_derived_from_the_site_postcode()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakeFlatPayload(
            accreditationYear: 2026,
            wasteProcessingType: "reprocessor",
            siteAddressPostcode: "SW1A 1AA", // England site
            companyRegisterAddressPostcode: "EH1 1AA" // Scotland registered office — must be ignored
        );

        var reference = generator.Generate(payload);

        Assert.Equal("EA", reference.Substring(4, 2));
        Assert.Equal("AP26EA500021AAGL", reference);
    }

    [Fact]
    public void AC02_payload_without_wasteProcessingType_falls_back_to_the_site_postcode()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakeFlatPayload(
            accreditationYear: 2026,
            siteAddressPostcode: "SW1A 1AA",
            companyRegisterAddressPostcode: "EH1 1AA"
        );

        var reference = generator.Generate(payload);

        // No wasteProcessingType (e.g. case-management admin UI payloads) behaves like a reprocessor.
        Assert.Equal("EA", reference.Substring(4, 2));
    }

    [Fact]
    public void AC01_exporter_with_no_registered_office_postcode_fails_open_to_England()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakeFlatPayload(
            accreditationYear: 2026,
            wasteProcessingType: "exporter",
            siteAddressPostcode: "EH1 1AA", // Scotland site — must be ignored for an exporter
            companyRegisterAddressPostcode: null
        );

        var reference = generator.Generate(payload);

        Assert.Equal("EA", reference.Substring(4, 2));
    }

    [Fact]
    public void AC01_exporter_with_no_registered_office_postcode_logs_a_warning()
    {
        var logger = new CapturingLogger<ApplicationReferenceGenerator>();
        var generator = new ApplicationReferenceGenerator(logger: logger);
        var payload = MakeFlatPayload(
            accreditationYear: 2026,
            wasteProcessingType: "exporter",
            companyRegisterAddressPostcode: null
        );

        generator.Generate(payload);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
    }

    [Fact]
    public void Generate_does_not_log_when_exporter_has_a_registered_office_postcode()
    {
        var logger = new CapturingLogger<ApplicationReferenceGenerator>();
        var generator = new ApplicationReferenceGenerator(logger: logger);
        var payload = MakeFlatPayload(
            accreditationYear: 2026,
            wasteProcessingType: "exporter",
            companyRegisterAddressPostcode: "EH1 1AA"
        );

        generator.Generate(payload);

        Assert.Empty(logger.Entries);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Theory]
    [InlineData("Exporter")]
    [InlineData("EXPORTER")]
    public void AC01_wasteProcessingType_match_is_case_insensitive(string wasteProcessingType)
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakeFlatPayload(
            accreditationYear: 2026,
            wasteProcessingType: wasteProcessingType,
            siteAddressPostcode: "SW1A 1AA",
            companyRegisterAddressPostcode: "EH1 1AA"
        );

        var reference = generator.Generate(payload);

        Assert.Equal("SE", reference.Substring(4, 2));
    }

    [Theory]
    [InlineData("EH1 1AA", "SE")] // Scotland — SEPA
    [InlineData("CF10 1AA", "NR")] // Wales — NRW
    [InlineData("BT1 1AA", "NI")] // Northern Ireland — DAERA
    [InlineData("SW1A 1AA", "EA")] // England — Environment Agency
    public void AC03_reference_maps_to_one_of_the_four_regional_regulators(
        string postcode,
        string expectedAgency
    )
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakeFlatPayload(accreditationYear: 2026, siteAddressPostcode: postcode);

        var reference = generator.Generate(payload);

        Assert.Equal(expectedAgency, reference.Substring(4, 2));
    }

    [Fact]
    public void AC04_organisationId_longer_than_five_characters_is_limited_to_the_last_five()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakeFlatPayload(
            accreditationYear: 2026,
            operatorOrganisationId: "6a2fcd74e16883c137d01188"
        );

        var reference = generator.Generate(payload);

        Assert.DoesNotContain(
            "6a2fcd74e16883c137d0",
            reference,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Contains("01188", reference, StringComparison.OrdinalIgnoreCase);
    }
}
