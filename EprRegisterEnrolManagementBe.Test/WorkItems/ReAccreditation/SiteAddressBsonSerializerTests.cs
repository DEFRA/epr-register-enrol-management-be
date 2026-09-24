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

    [Theory]
    [MemberData(nameof(NoAddressCases))]
    public void Absent_blank_or_unusable_values_read_as_null(BsonValue? siteAddress)
    {
        var payload = Deserialise(siteAddress);

        Assert.Null(payload.SiteAddress);
        Assert.Equal("Acme Ltd", payload.OrganisationName);
    }

    public static TheoryData<BsonValue?> NoAddressCases() => new()
    {
        null,
        BsonNull.Value,
        "   ",
        new BsonDocument(),
        new BsonDocument { ["postcode"] = "BS1 4DJ" },
        new BsonInt32(42),
    };
}
