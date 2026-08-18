using System.Text.RegularExpressions;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// epr-accreditation-id-format: unit tests for
/// <see cref="AccreditationIdGenerator"/>. The generator owns format
/// (<c>A{Year:2}{Agency:1}{OperatorType:1}{OrgId:6}{PostcodeSuffix:3}{Material:2}</c>,
/// 16 characters fixed width), the material lookup table, and
/// cross-collection uniqueness via the injected <see cref="IAccreditationIdLookup"/>;
/// the approval service is not exercised here.
/// </summary>
public class AccreditationIdGeneratorTests
{
    private static readonly Regex s_format =
        new("^A[0-9]{2}[ESWN][RX][0-9A-Z-]{6}[0-9A-Z]{3}[A-Z]{2}$", RegexOptions.CultureInvariant);

    private static AccreditationIdGenerator Build(
        IAccreditationIdLookup? lookup = null, INationResolver? nationResolver = null) =>
        new(lookup ?? NeverCollides(), nationResolver ?? new NationResolver());

    private static IAccreditationIdLookup NeverCollides()
    {
        var lookup = Substitute.For<IAccreditationIdLookup>();
        lookup.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        return lookup;
    }

    private static BsonDocument Payload(
        string? material = "plastic",
        string? wasteProcessingType = "reprocessor",
        string? operatorOrganisationId = "ORG123456",
        string? siteAddressPostcode = "EC1A 1BB",
        string? companyRegisterAddressPostcode = null)
    {
        var doc = new BsonDocument();
        if (material is not null)
        {
            doc["material"] = material;
        }
        if (wasteProcessingType is not null)
        {
            doc["wasteProcessingType"] = wasteProcessingType;
        }
        if (operatorOrganisationId is not null)
        {
            doc["operatorOrganisationId"] = operatorOrganisationId;
        }
        if (siteAddressPostcode is not null)
        {
            doc["siteAddressPostcode"] = siteAddressPostcode;
        }
        if (companyRegisterAddressPostcode is not null)
        {
            doc["companyRegisterAddressPostcode"] = companyRegisterAddressPostcode;
        }
        return doc;
    }

    [Theory]
    [InlineData("plastic", "PL")]
    [InlineData("steel", "ST")]
    [InlineData("wood", "WO")]
    [InlineData("aluminium", "AL")]
    [InlineData("glass", "GL")]
    public async Task GenerateAsync_uses_the_material_lookup_table(string material, string expected)
    {
        var sut = Build();

        var id = await sut.GenerateAsync(
            Payload(material: material), 2027, TestContext.Current.CancellationToken);

        Assert.Matches(s_format, id);
        Assert.EndsWith(expected, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown-material")]
    public async Task GenerateAsync_falls_back_to_XX_for_unmapped_material(string? material)
    {
        var sut = Build();

        var id = await sut.GenerateAsync(
            Payload(material: material), 2030, TestContext.Current.CancellationToken);

        Assert.Matches(s_format, id);
        Assert.EndsWith("XX", id);
    }

    [Fact]
    public async Task GenerateAsync_uses_last_two_digits_of_the_supplied_year()
    {
        var sut = Build();

        var id = await sut.GenerateAsync(Payload(), 2028, TestContext.Current.CancellationToken);

        Assert.Equal("A28", id[..3]);
    }

    [Theory]
    [InlineData("EC1A 1BB", "E")] // England
    [InlineData("AB10 1AA", "S")] // Scotland
    [InlineData("CF10 1AA", "W")] // Wales
    [InlineData("BT7 1AA", "N")] // Northern Ireland
    public async Task GenerateAsync_derives_the_agency_letter_from_the_site_postcode(
        string postcode, string expectedAgency)
    {
        var sut = Build();

        var id = await sut.GenerateAsync(
            Payload(siteAddressPostcode: postcode), 2027, TestContext.Current.CancellationToken);

        Assert.Equal(expectedAgency, id[3..4]);
    }

    [Theory]
    [InlineData("reprocessor", "R")]
    [InlineData(null, "R")]
    [InlineData("exporter", "X")]
    public async Task GenerateAsync_derives_the_operator_type_letter_from_waste_processing_type(
        string? wasteProcessingType, string expectedLetter)
    {
        var sut = Build();

        var id = await sut.GenerateAsync(
            Payload(
                wasteProcessingType: wasteProcessingType,
                companyRegisterAddressPostcode: "EC1A 1BB"),
            2027,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedLetter, id[4..5]);
    }

    [Fact]
    public async Task GenerateAsync_uses_the_last_six_characters_of_the_organisation_id()
    {
        var sut = Build();

        var id = await sut.GenerateAsync(
            Payload(operatorOrganisationId: "org-full-payload-001"),
            2027,
            TestContext.Current.CancellationToken);

        Assert.Equal("AD-001".ToUpperInvariant(), id[5..11]);
    }

    [Fact]
    public async Task GenerateAsync_left_pads_a_short_organisation_id()
    {
        var sut = Build();

        var id = await sut.GenerateAsync(
            Payload(operatorOrganisationId: "42"), 2027, TestContext.Current.CancellationToken);

        Assert.Equal("000042", id[5..11]);
    }

    [Fact]
    public async Task GenerateAsync_uses_the_last_three_characters_of_the_postcode()
    {
        var sut = Build();

        var id = await sut.GenerateAsync(
            Payload(siteAddressPostcode: "EC1A 1BB"), 2027, TestContext.Current.CancellationToken);

        Assert.Equal("1BB", id[11..14]);
    }

    [Fact]
    public async Task GenerateAsync_produces_a_fixed_16_character_id()
    {
        var sut = Build();

        var id = await sut.GenerateAsync(Payload(), 2027, TestContext.Current.CancellationToken);

        Assert.Equal(16, id.Length);
    }

    [Fact]
    public async Task GenerateAsync_is_deterministic_for_the_same_payload_and_year()
    {
        var sut = Build();
        var payload = Payload();

        var first = await sut.GenerateAsync(payload, 2027, TestContext.Current.CancellationToken);
        var second = await sut.GenerateAsync(payload, 2027, TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task GenerateAsync_disambiguates_the_final_character_on_collision()
    {
        var lookup = Substitute.For<IAccreditationIdLookup>();
        // First two probes collide, third is unique.
        lookup.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true), Task.FromResult(true), Task.FromResult(false));
        var sut = Build(lookup);
        var payload = Payload();

        var id = await sut.GenerateAsync(payload, 2027, TestContext.Current.CancellationToken);

        // The disambiguator can overwrite the material segment's final
        // letter with a digit, so the full s_format (which requires two
        // letters there) no longer applies — only shape and length do.
        Assert.Equal(16, id.Length);
        Assert.Equal('3', id[^1]);
        await lookup.Received(3).ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Deterministic base candidate, minus the disambiguated final char.
        var basePrefix = id[..^1];
        Assert.Equal(basePrefix, (await sut.GenerateAsync(payload, 2027, TestContext.Current.CancellationToken))[..^1]);
    }

    [Fact]
    public async Task GenerateAsync_throws_when_collisions_exceed_max_attempts()
    {
        var lookup = Substitute.For<IAccreditationIdLookup>();
        lookup.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        var sut = Build(lookup);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.GenerateAsync(Payload(), 2027, TestContext.Current.CancellationToken));

        await lookup.Received(AccreditationIdGenerator.MaxAttempts)
            .ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateAsync_rejects_a_null_payload()
    {
        var sut = Build();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => sut.GenerateAsync(null!, 2027, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GenerateAsync_exporter_uses_registered_office_postcode()
    {
        var sut = Build();
        // Exporter postcode is companyRegisterAddressPostcode, not the site
        // postcode — use different values so we can tell which was used.
        var payload = Payload(
            wasteProcessingType: "exporter",
            siteAddressPostcode: "SW1A 1AA",
            companyRegisterAddressPostcode: "EH1 1AA");

        var id = await sut.GenerateAsync(payload, 2027, TestContext.Current.CancellationToken);

        Assert.Equal('S', id[3]); // Scotland agency letter, from EH1 not SW1A.
        Assert.Equal('X', id[4]); // exporter operator-type letter.
        Assert.Equal("1AA", id[11..14]);
    }

    [Fact]
    public async Task GenerateAsync_exporter_with_no_registered_office_postcode_falls_open_to_England()
    {
        var sut = Build();
        var payload = Payload(
            wasteProcessingType: "exporter",
            siteAddressPostcode: "AB10 1AA", // would be Scotland if used, but must not be
            companyRegisterAddressPostcode: null);

        var id = await sut.GenerateAsync(payload, 2027, TestContext.Current.CancellationToken);

        Assert.Equal('E', id[3]);
        Assert.Equal('X', id[4]);
    }

    [Fact]
    public async Task GenerateAsync_reads_the_site_postcode_from_a_nested_siteAddress_document()
    {
        var sut = Build();
        var payload = Payload(siteAddressPostcode: null);
        payload["siteAddress"] = new BsonDocument { ["postcode"] = "CF10 1AA" };

        var id = await sut.GenerateAsync(payload, 2027, TestContext.Current.CancellationToken);

        Assert.Equal('W', id[3]); // Wales
    }

    [Fact]
    public async Task GenerateAsync_treats_a_non_document_siteAddress_as_no_postcode()
    {
        var sut = Build();
        var payload = Payload(siteAddressPostcode: null);
        payload["siteAddress"] = "1 Example Street";

        var id = await sut.GenerateAsync(payload, 2027, TestContext.Current.CancellationToken);

        Assert.Equal('E', id[3]); // default England fallback.
    }

    [Fact]
    public async Task GenerateAsync_uses_organisation_id_unchanged_when_exactly_six_characters()
    {
        var sut = Build();
        var payload = Payload(operatorOrganisationId: "ABC123");

        var id = await sut.GenerateAsync(payload, 2027, TestContext.Current.CancellationToken);

        Assert.Equal("ABC123", id[5..11]);
    }

    [Fact]
    public async Task GenerateAsync_uses_postcode_suffix_unchanged_when_exactly_three_characters()
    {
        var sut = Build();
        var payload = Payload(siteAddressPostcode: "AB1"); // compacts to exactly 3 chars.

        var id = await sut.GenerateAsync(payload, 2027, TestContext.Current.CancellationToken);

        Assert.Equal("AB1", id[11..14]);
    }

    [Fact]
    public async Task GenerateAsync_disambiguates_on_the_final_permitted_attempt()
    {
        var lookup = Substitute.For<IAccreditationIdLookup>();
        // MaxAttempts is 5: four collisions, then success on the fifth probe.
        lookup.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(true), Task.FromResult(true), Task.FromResult(true),
                Task.FromResult(true), Task.FromResult(false));
        var sut = Build(lookup);

        var id = await sut.GenerateAsync(Payload(), 2027, TestContext.Current.CancellationToken);

        Assert.Equal('5', id[^1]);
        await lookup.Received(5).ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
