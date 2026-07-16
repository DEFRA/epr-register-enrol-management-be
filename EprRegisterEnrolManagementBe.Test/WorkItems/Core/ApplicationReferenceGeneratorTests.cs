using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-318: unit coverage for the deterministic, payload-derived
/// applicationReference generator. Format:
/// APP + 2-digit year + 2-char agency + organisationId + last 3 chars of
/// postcode + first 2 chars of material, upper-cased, capped at
/// <see cref="ApplicationReferenceGenerator.MaxLength"/> chars (this value
/// doubles as a BACS payment reference).
/// </summary>
public sealed class ApplicationReferenceGeneratorTests
{
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
            doc["siteAddressPostcode"] = siteAddressPostcode;
        if (material is not null)
            doc["material"] = material;
        return doc;
    }

    [Fact]
    public void Generate_builds_expected_reference_for_england_postcode()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(accreditationYear: 2026);

        var reference = generator.Generate(payload);

        Assert.Equal("APP26EA500021AAGL", reference);
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

        Assert.Equal(expectedAgency, reference.Substring(5, 2));
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
        Assert.Equal("APP26EA500021AAGL", reference);
    }

    [Fact]
    public void Generate_falls_back_to_current_year_when_accreditationYear_missing()
    {
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var generator = new ApplicationReferenceGenerator(fakeTime);
        var payload = MakePayload(accreditationYear: null);

        var reference = generator.Generate(payload);

        Assert.StartsWith("APP31", reference);
    }

    [Fact]
    public void Generate_truncates_to_MaxLength_when_organisationId_is_long()
    {
        var generator = new ApplicationReferenceGenerator();
        var payload = MakePayload(
            accreditationYear: 2026,
            operatorOrganisationId: "6a2fcd74e16883c137d01188"
        );

        var reference = generator.Generate(payload);

        Assert.Equal(ApplicationReferenceGenerator.MaxLength, reference.Length);
        Assert.Equal("APP26EA6A2FCD74E16", reference);
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

        Assert.Equal("APP26EA", reference);
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
}
