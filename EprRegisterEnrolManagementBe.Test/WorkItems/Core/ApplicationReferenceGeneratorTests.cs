using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-318: unit coverage for the deterministic, payload-derived
/// applicationReference generator. Format:
/// AP + 2-digit year + 2-char agency + organisation segment (RA-503: the
/// numeric operatorOrgNumber zero-padded to 6 digits when present,
/// otherwise the last 5 chars of the legacy operatorOrganisationId) +
/// last 3 chars of postcode + first 2 chars of material, upper-cased.
/// No longer capped to a fixed length (RA-503 — this value is no longer
/// used as a BACS payment reference). Attempts beyond the first (the
/// collision-retry path) append a disambiguator character.
/// </summary>
public sealed class ApplicationReferenceGeneratorTests
{
    // The case-management admin UI nests the postcode under
    // payload.siteAddress.postcode (matching its Joi schema) — this fixture
    // mirrors that shape. The operator-facing backend BFF instead sends a
    // flat siteAddressPostcode key alongside a string siteAddress
    // (HttpCaseWorkingApiAdapter.BuildPayload); see MakeFlatPayload below
    // for that shape.
    private static BsonDocument MakePayload(
        object? accreditationYear = null,
        string? operatorOrganisationId = "50002",
        int? operatorOrgNumber = null,
        string? siteAddressPostcode = "SW1A 1AA",
        string? material = "Glass",
        string? nation = null
    )
    {
        var doc = new BsonDocument();
        if (accreditationYear is not null)
            doc["accreditationYear"] = BsonValue.Create(accreditationYear);
        if (operatorOrganisationId is not null)
            doc["operatorOrganisationId"] = operatorOrganisationId;
        if (operatorOrgNumber is not null)
            doc["operatorOrgNumber"] = operatorOrgNumber.Value;
        if (siteAddressPostcode is not null)
            doc["siteAddress"] = new BsonDocument { ["postcode"] = siteAddressPostcode };
        if (material is not null)
            doc["material"] = material;
        if (nation is not null)
            doc["nation"] = nation;
        return doc;
    }

    // Mirrors the operator-facing backend BFF's payload shape
    // (HttpCaseWorkingApiAdapter.BuildPayload): siteAddress is a plain
    // string and the postcode is a separate flat siteAddressPostcode key.
    private static BsonDocument MakeFlatPayload(
        object? accreditationYear = null,
        string? operatorOrganisationId = "50002",
        int? operatorOrgNumber = null,
        string? siteAddressPostcode = "SW1A 1AA",
        string? material = "Glass",
        string? wasteProcessingType = null,
        string? companyRegisterAddressPostcode = null,
        string? nation = null
    )
    {
        var doc = new BsonDocument();
        if (accreditationYear is not null)
            doc["accreditationYear"] = BsonValue.Create(accreditationYear);
        if (operatorOrganisationId is not null)
            doc["operatorOrganisationId"] = operatorOrganisationId;
        if (operatorOrgNumber is not null)
            doc["operatorOrgNumber"] = operatorOrgNumber.Value;
        doc["siteAddress"] = "1 Example Street, Example Town";
        if (siteAddressPostcode is not null)
            doc["siteAddressPostcode"] = siteAddressPostcode;
        if (material is not null)
            doc["material"] = material;
        if (wasteProcessingType is not null)
            doc["wasteProcessingType"] = wasteProcessingType;
        if (companyRegisterAddressPostcode is not null)
            doc["companyRegisterAddressPostcode"] = companyRegisterAddressPostcode;
        if (nation is not null)
            doc["nation"] = nation;
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

    // RA-526: payload.nation is the authoritative source once present - it must win even when
    // it disagrees with what the postcode would have suggested, since postcode-prefix matching
    // is exactly the unreliable heuristic RA-526 replaced it with. LL14 (Wrexham) is the real
    // production postcode this bug was diagnosed against.
    [Theory]
    [InlineData("England", "SW1A 1AA", "EA")]
    [InlineData("Scotland", "SW1A 1AA", "SE")] // nation wins even against an England postcode
    [InlineData("Wales", "SW1A 1AA", "NR")] // nation wins even against an England postcode
    [InlineData("NorthernIreland", "SW1A 1AA", "NI")] // nation wins even against an England postcode
    [InlineData("Wales", "LL14 5NT", "NR")] // the real diagnosed case: nation and postcode agree
    [InlineData("scotland", "SW1A 1AA", "SE")] // case-insensitive, matching ResolveNation's own parse
    public void Generate_prefers_payload_nation_over_postcode_for_agency_code(
        string nation, string postcode, string expectedAgency)
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(accreditationYear: 2026, siteAddressPostcode: postcode, nation: nation);

        var reference = generator.Generate(payload);

        Assert.Equal(expectedAgency, reference.Substring(4, 2));
    }

    [Theory]
    [InlineData("Atlantis")] // unrecognised nation string
    [InlineData("")] // blank
    public void Generate_falls_back_to_postcode_when_nation_is_unrecognised(string nation)
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(accreditationYear: 2026, siteAddressPostcode: "EH1 1AA", nation: nation);

        var reference = generator.Generate(payload);

        // Falls through to the postcode-derived code (Scotland, EH1) rather than defaulting to
        // England outright - an unrecognised nation is not the same as an absent one, but neither
        // can be trusted, so this is the same "derive from what's actually there" behaviour as
        // no nation field at all.
        Assert.Equal("SE", reference.Substring(4, 2));
    }

    [Fact]
    public void Generate_falls_back_to_postcode_when_nation_is_absent()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(accreditationYear: 2026, siteAddressPostcode: "CF10 1AA", nation: null);

        var reference = generator.Generate(payload);

        Assert.Equal("NR", reference.Substring(4, 2));
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
    }

    // RA-503: operatorOrgNumber (numeric, operator/regulator-safe) takes priority over the
    // legacy operatorOrganisationId (ReEx's internal ObjectId) when both are present.
    [Fact]
    public void Generate_prefers_the_numeric_operatorOrgNumber_over_operatorOrganisationId()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(
            accreditationYear: 2026,
            operatorOrganisationId: "6a2fcd74e16883c137d01188",
            operatorOrgNumber: 500500
        );

        var reference = generator.Generate(payload);

        Assert.Equal("AP26EA5005001AAGL", reference);
        Assert.DoesNotContain("01188", reference, StringComparison.OrdinalIgnoreCase);
    }

    // RA-503: falls back to the legacy last-5-characters-of-operatorOrganisationId behaviour
    // when operatorOrgNumber is absent - e.g. the deploy gap before the upstream backend sends
    // it, so the generator never regresses below its pre-fix behaviour.
    [Fact]
    public void Generate_falls_back_to_operatorOrganisationId_when_operatorOrgNumber_is_absent()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(accreditationYear: 2026, operatorOrganisationId: "50002");

        var reference = generator.Generate(payload);

        Assert.Equal("AP26EA500021AAGL", reference);
    }

    // RA-503: operatorOrgNumber is zero-padded to 6 digits, matching the RegulatoryNumberGenerator
    // format this value is designed to be consistent with.
    [Fact]
    public void Generate_zero_pads_operatorOrgNumber_to_six_digits()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(accreditationYear: 2026, operatorOrgNumber: 42);

        var reference = generator.Generate(payload);

        Assert.Equal("AP26EA0000421AAGL", reference);
    }

    // RA-503: operatorOrgNumber must be validated, not trusted from its BSON type category alone
    // - IsNumeric admits Int64/Double/Decimal128, none of which ToInt32() can convert safely from
    // (silent wraparound for Int64/Double, OverflowException for Decimal128), and D6 is a MINIMUM
    // width, not a cap, so an out-of-range value would otherwise widen the reference unpredictably.
    // Every invalid case falls back to the legacy operatorOrganisationId behaviour rather than
    // embedding a garbage/malformed segment or throwing.
    [Theory]
    [InlineData(-1)]
    [InlineData(1_000_000)]
    public void Generate_rejects_an_out_of_range_operatorOrgNumber_and_falls_back(int invalidValue)
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(
            accreditationYear: 2026,
            operatorOrganisationId: "6a2fcd74e16883c137d01188",
            operatorOrgNumber: invalidValue
        );

        var reference = generator.Generate(payload);

        Assert.Equal("AP26EA011881AAGL", reference);
    }

    [Fact]
    public void Generate_rejects_a_non_integral_operatorOrgNumber_and_falls_back()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(
            accreditationYear: 2026,
            operatorOrganisationId: "6a2fcd74e16883c137d01188"
        );
        payload["operatorOrgNumber"] = 500500.7;

        var reference = generator.Generate(payload);

        Assert.Equal("AP26EA011881AAGL", reference);
    }

    [Fact]
    public void Generate_rejects_an_out_of_range_Int64_operatorOrgNumber_without_wrapping()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(
            accreditationYear: 2026,
            operatorOrganisationId: "6a2fcd74e16883c137d01188"
        );
        // (int)long.MaxValue silently wraps to -1 - confirm that never reaches the reference.
        payload["operatorOrgNumber"] = long.MaxValue;

        var reference = generator.Generate(payload);

        Assert.Equal("AP26EA011881AAGL", reference);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Generate_rejects_a_non_finite_operatorOrgNumber_without_throwing(
        double invalidValue
    )
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(
            accreditationYear: 2026,
            operatorOrganisationId: "6a2fcd74e16883c137d01188"
        );
        // decimal cannot represent NaN/Infinity, so BsonValue.ToDecimal() throws for these -
        // confirm Generate degrades to the fallback instead of throwing past the caller.
        payload["operatorOrgNumber"] = invalidValue;

        var reference = generator.Generate(payload);

        Assert.Equal("AP26EA011881AAGL", reference);
    }

    [Fact]
    public void Generate_rejects_a_Decimal128_operatorOrgNumber_outside_the_valid_range_without_throwing()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(
            accreditationYear: 2026,
            operatorOrganisationId: "6a2fcd74e16883c137d01188"
        );
        // decimal.MaxValue converts to decimal without overflow, so this is rejected by the
        // >999999 range check rather than the OverflowException catch - either way, Generate
        // must degrade to the fallback instead of throwing or embedding a garbage segment.
        payload["operatorOrgNumber"] = new BsonDecimal128(decimal.MaxValue);

        var reference = generator.Generate(payload);

        Assert.Equal("AP26EA011881AAGL", reference);
    }

    [Fact]
    public void Generate_accepts_operatorOrgNumber_at_the_upper_bound()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(accreditationYear: 2026, operatorOrgNumber: 999999);

        var reference = generator.Generate(payload);

        Assert.Equal("AP26EA9999991AAGL", reference);
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
    public void Generate_with_attempt_greater_than_one_differs_from_the_first_attempt()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(accreditationYear: 2026);

        var first = generator.Generate(payload, attempt: 1);
        var second = generator.Generate(payload, attempt: 2);

        Assert.NotEqual(first, second);
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

    // RA-503: the disambiguator branch always appends now (no MaxLength truncation to replace a
    // character within), so this covers Generate's only disambiguator path.
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

    // RA-526: pins the whole reference, not just the agency-code segment, on the nation path -
    // proving ResolveRegulatorPostcode's exporter->registered-office selection still feeds the
    // postcode-suffix segment even when payload.nation (not postcode) decides the agency code,
    // and that the two segments can legitimately come from different sources on the same call.
    [Fact]
    public void Generate_full_reference_when_nation_decides_agency_code_but_postcode_still_feeds_the_suffix()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakeFlatPayload(
            accreditationYear: 2026,
            wasteProcessingType: "exporter",
            siteAddressPostcode: "SW1A 1AA", // England site — must be ignored for an exporter
            companyRegisterAddressPostcode: "EH1 1AA", // feeds the postcode-suffix segment (1AA)
            nation: "Wales" // disagrees with the Scotland postcode - nation must win the agency code
        );

        var reference = generator.Generate(payload);

        Assert.Equal("NR", reference.Substring(4, 2));
        Assert.Equal("AP26NR500021AAGL", reference);
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

    [Fact]
    public void Generate_falls_open_to_England_when_postcode_has_no_letter_prefix()
    {
        // ExtractAreaCode returns null when the postcode starts with a
        // digit (or is otherwise unparseable), which must fail open to
        // the default England agency rather than throwing.
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(accreditationYear: 2026, siteAddressPostcode: "1AA");

        var reference = generator.Generate(payload);

        Assert.Equal("EA", reference.Substring(4, 2));
    }

    [Fact]
    public void Generate_returns_null_postcode_when_siteAddress_is_present_but_not_a_document()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = new BsonDocument
        {
            ["accreditationYear"] = 2026,
            ["operatorOrganisationId"] = "50002",
            ["siteAddress"] = "not a nested document",
            ["material"] = "Glass",
        };

        var reference = generator.Generate(payload);

        // No postcode suffix contributed, and agency falls open to England.
        Assert.Equal("EA", reference.Substring(4, 2));
    }

    [Fact]
    public void Generate_returns_null_postcode_when_siteAddress_document_has_no_postcode_key()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = new BsonDocument
        {
            ["accreditationYear"] = 2026,
            ["operatorOrganisationId"] = "50002",
            ["siteAddress"] = new BsonDocument { ["line1"] = "1 Example Street" },
            ["material"] = "Glass",
        };

        var reference = generator.Generate(payload);

        Assert.Equal("EA", reference.Substring(4, 2));
    }

    [Fact]
    public void Generate_disambiguator_wraps_to_zero_on_the_tenth_attempt()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(accreditationYear: 2026);

        var reference = generator.Generate(payload, attempt: 10);

        Assert.EndsWith("0", reference);
    }

    [Fact]
    public void Generate_postcode_suffix_uses_whole_compact_postcode_when_three_characters_or_fewer()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(accreditationYear: 2026, siteAddressPostcode: "W1A");

        var reference = generator.Generate(payload);

        // agency(2) + organisationId(5) then the 3-char postcode suffix.
        Assert.Equal("W1A", reference.Substring(4 + 2 + 5, 3));
    }

    [Fact]
    public void Generate_material_prefix_uses_whole_material_when_two_characters_or_fewer()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(accreditationYear: 2026, material: "Pl");

        var reference = generator.Generate(payload);

        Assert.EndsWith("PL", reference, StringComparison.OrdinalIgnoreCase);
    }
}
