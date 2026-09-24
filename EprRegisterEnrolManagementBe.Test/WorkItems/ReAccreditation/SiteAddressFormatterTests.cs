using System.Text.Json;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-581. <c>payload.siteAddress</c> is either a flat string (operator backend)
/// or a nested { line1, line2, town, postcode } document (case management form).
/// It is read raw by <see cref="SiteAddressFormatter"/> and must stay UNMODELLED
/// on <see cref="ReAccreditationPayload"/>: that record is round-tripped through
/// <c>ToBsonDocument()</c> and merged back with overwriteExistingElements on
/// duly-making and approval, which would otherwise clobber the nested document
/// and destroy the item's only copy of its postcode.
/// </summary>
public class SiteAddressFormatterTests
{
    private static BsonDocument Payload(BsonValue? siteAddress)
    {
        var document = new BsonDocument { ["organisationName"] = "Acme Ltd" };
        if (siteAddress is not null)
        {
            document["siteAddress"] = siteAddress;
        }

        return document;
    }

    [Fact]
    public void Flat_string_is_read_as_is()
    {
        Assert.Equal(
            "1 Main St, Leeds, LS1 1AB",
            SiteAddressFormatter.Format(Payload("1 Main St, Leeds, LS1 1AB")));
    }

    [Fact]
    public void Nested_document_is_flattened_without_the_postcode()
    {
        var address = SiteAddressFormatter.Format(Payload(new BsonDocument
        {
            ["line1"] = "12 Industrial Way",
            ["line2"] = "Parkside Estate",
            ["town"] = "Bristol",
            ["postcode"] = "BS1 4DJ",
        }));

        Assert.Equal("12 Industrial Way, Parkside Estate, Bristol", address);
    }

    [Fact]
    public void Nested_document_skips_blank_and_non_string_lines()
    {
        var address = SiteAddressFormatter.Format(Payload(new BsonDocument
        {
            ["line1"] = "12 Industrial Way",
            ["line2"] = "  ",
            ["town"] = 5,
        }));

        Assert.Equal("12 Industrial Way", address);
    }

    [Fact]
    public void Null_payload_reads_as_null() => Assert.Null(SiteAddressFormatter.Format(null));

    [Fact]
    public void Absent_key_reads_as_null() => Assert.Null(SiteAddressFormatter.Format(Payload(null)));

    [Fact]
    public void Bson_null_reads_as_null() => Assert.Null(SiteAddressFormatter.Format(Payload(BsonNull.Value)));

    [Fact]
    public void Blank_string_reads_as_null() => Assert.Null(SiteAddressFormatter.Format(Payload("   ")));

    [Fact]
    public void Empty_document_reads_as_null() =>
        Assert.Null(SiteAddressFormatter.Format(Payload(new BsonDocument())));

    [Fact]
    public void Document_with_only_a_postcode_reads_as_null() =>
        Assert.Null(SiteAddressFormatter.Format(Payload(new BsonDocument { ["postcode"] = "BS1 4DJ" })));

    [Fact]
    public void Unusable_value_type_reads_as_null() =>
        Assert.Null(SiteAddressFormatter.Format(Payload(new BsonInt32(42))));

    /// <summary>
    /// The exact cycle ReAccreditationDulyMakingService / ReAccreditationApprovalService run:
    /// deserialise, mutate, ToBsonDocument, merge over the stored document.
    /// </summary>
    [Fact]
    public void Nested_siteAddress_survives_the_payload_round_trip_merge()
    {
        var stored = new BsonDocument
        {
            ["organisationName"] = "Acme Ltd",
            ["siteAddress"] = new BsonDocument
            {
                ["line1"] = "12 Industrial Way",
                ["town"] = "Bristol",
                ["postcode"] = "BS1 4DJ",
            },
        };

        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(stored);
        var updated = payload with { PaymentDate = new DateOnly(2026, 9, 1) };
        var merged = stored.DeepClone().AsBsonDocument;
        merged.Merge(updated.ToBsonDocument(), overwriteExistingElements: true);

        Assert.Equal(stored["siteAddress"], merged["siteAddress"]);
        Assert.Equal("BS1 4DJ", merged["siteAddress"].AsBsonDocument["postcode"].AsString);
    }

    [Fact]
    public void Flat_siteAddress_survives_the_payload_round_trip_merge()
    {
        var stored = new BsonDocument
        {
            ["organisationName"] = "Acme Ltd",
            ["siteAddress"] = "1 Main St, Leeds, LS1 1AB",
        };

        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(stored);
        var merged = stored.DeepClone().AsBsonDocument;
        merged.Merge(payload.ToBsonDocument(), overwriteExistingElements: true);

        Assert.Equal("1 Main St, Leeds, LS1 1AB", merged["siteAddress"].AsString);
    }

    /// <summary>
    /// Endpoints such as prior-year deserialise the payload with System.Text.Json;
    /// a nested siteAddress must not break that (it is simply unmodelled).
    /// </summary>
    [Fact]
    public void Nested_siteAddress_does_not_break_json_deserialisation_of_the_payload()
    {
        var payload = JsonSerializer.Deserialize<ReAccreditationPayload>(
            """{ "organisationName": "Acme Ltd", "siteAddress": { "line1": "12 Industrial Way", "postcode": "BS1 4DJ" } }""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Equal("Acme Ltd", payload!.OrganisationName);
    }
}
