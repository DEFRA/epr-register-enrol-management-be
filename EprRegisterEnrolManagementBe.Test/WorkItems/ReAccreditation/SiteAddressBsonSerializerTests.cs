using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-581 regression: <c>ReAccreditationPayload.SiteAddress</c> is a string, but
/// work items created through the case management form store <c>siteAddress</c>
/// as a nested document. Deserialising that used to throw and broke every
/// reader of the payload (found by the management-be E2E job, where every
/// UI-created item hit it).
/// </summary>
public class SiteAddressBsonSerializerTests
{
    private static ReAccreditationPayload Deserialise(BsonValue? siteAddress)
    {
        var document = new BsonDocument { ["organisationName"] = "Acme Ltd" };
        if (siteAddress is not null)
        {
            document["siteAddress"] = siteAddress;
        }

        return BsonSerializer.Deserialize<ReAccreditationPayload>(document);
    }

    [Fact]
    public void Flat_string_is_read_as_is()
    {
        var payload = Deserialise("1 Main St, Leeds, LS1 1AB");

        Assert.Equal("1 Main St, Leeds, LS1 1AB", payload.SiteAddress);
    }

    [Fact]
    public void Nested_document_is_flattened_without_the_postcode()
    {
        var payload = Deserialise(new BsonDocument
        {
            ["line1"] = "12 Industrial Way",
            ["line2"] = "Parkside Estate",
            ["town"] = "Bristol",
            ["postcode"] = "BS1 4DJ",
        });

        Assert.Equal("12 Industrial Way, Parkside Estate, Bristol", payload.SiteAddress);
        Assert.Equal("Acme Ltd", payload.OrganisationName);
    }

    [Fact]
    public void Nested_document_skips_blank_and_missing_lines()
    {
        var payload = Deserialise(new BsonDocument
        {
            ["line1"] = "12 Industrial Way",
            ["line2"] = "  ",
            ["town"] = "Bristol",
        });

        Assert.Equal("12 Industrial Way, Bristol", payload.SiteAddress);
    }

    [Fact]
    public void Absent_key_reads_as_null() => AssertNull(Deserialise(null));

    [Fact]
    public void Bson_null_reads_as_null() => AssertNull(Deserialise(BsonNull.Value));

    [Fact]
    public void Blank_string_reads_as_null() => AssertNull(Deserialise("   "));

    [Fact]
    public void Empty_document_reads_as_null() => AssertNull(Deserialise(new BsonDocument()));

    [Fact]
    public void Document_with_only_a_postcode_reads_as_null() =>
        AssertNull(Deserialise(new BsonDocument { ["postcode"] = "BS1 4DJ" }));

    [Fact]
    public void Unusable_value_type_reads_as_null() => AssertNull(Deserialise(new BsonInt32(42)));

    private static void AssertNull(ReAccreditationPayload payload)
    {
        Assert.Null(payload.SiteAddress);
        Assert.Equal("Acme Ltd", payload.OrganisationName);
    }
}
