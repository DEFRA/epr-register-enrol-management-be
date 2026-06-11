using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace EprRegisterEnrolManagementBe.Test.Utils.Mongo;

/// <summary>
/// RA-176 regression: <see cref="System.DateOnly"/> values (notably the
/// issued <c>AccreditationStartDate</c>) must serialise as a plain ISO date
/// string so the payload JSON returned to the BFF contains
/// <c>"accreditationStartDate": "2027-01-01"</c> rather than the driver's
/// default extended-JSON object <c>{"$date": ...}</c>, which the frontend
/// rendered as the literal "[object Object]".
/// </summary>
public class MongoConventionsDateOnlyTests
{
    static MongoConventionsDateOnlyTests() => MongoConventions.Register();

    [Fact]
    public void DateOnly_payload_field_serialises_as_iso_string_in_relaxed_json()
    {
        var payload = new ReAccreditationPayload
        {
            AccreditationId = "ACC-2027-P-AB12CD34",
            AccreditationStartDate = new DateOnly(2027, 1, 1),
            AccreditationYear = 2027
        };

        var json = WorkItemPayloadConverter.ToJson(payload.ToBsonDocument());

        Assert.Equal(
            "2027-01-01",
            json.GetProperty("accreditationStartDate").GetString());
    }

    [Fact]
    public void DateOnly_serialises_to_a_bson_string_not_a_datetime()
    {
        var payload = new ReAccreditationPayload
        {
            AccreditationStartDate = new DateOnly(2027, 1, 1)
        };

        var element = payload.ToBsonDocument()["accreditationStartDate"];

        Assert.Equal(BsonType.String, element.BsonType);
        Assert.Equal("2027-01-01", element.AsString);
    }

    [Fact]
    public void DateOnly_round_trips_from_iso_string()
    {
        var payload = new ReAccreditationPayload
        {
            AccreditationStartDate = new DateOnly(2027, 1, 1)
        };

        var roundTripped = BsonSerializer.Deserialize<ReAccreditationPayload>(
            payload.ToBsonDocument());

        Assert.Equal(new DateOnly(2027, 1, 1), roundTripped.AccreditationStartDate);
    }

    [Fact]
    public void DateOnly_still_reads_legacy_bson_datetime_documents()
    {
        // Documents written before RA-176 stored the start date as a BSON
        // DateTime. The string-representation serializer must remain
        // backward-compatible and read those values back as DateOnly.
        var legacy = new BsonDocument
        {
            ["accreditationStartDate"] =
                new DateTime(2025, 2, 3, 0, 0, 0, DateTimeKind.Utc)
        };

        var roundTripped =
            BsonSerializer.Deserialize<ReAccreditationPayload>(legacy);

        Assert.Equal(new DateOnly(2025, 2, 3), roundTripped.AccreditationStartDate);
    }
}
