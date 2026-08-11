using System.Text.Json;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// Regression tests for epr-b0x: <see cref="WorkItemPayloadConverter.ToJson"/>
/// must pin <c>JsonOutputMode.RelaxedExtendedJson</c> so numeric and date
/// payload fields render predictably for API consumers regardless of the
/// MongoDB driver's default output mode.
/// </summary>
public class WorkItemPayloadConverterTests
{
    [Fact]
    public void ToJson_emits_relaxed_extended_json_for_numbers_and_dates()
    {
        var date = new DateTime(2026, 4, 30, 12, 34, 56, DateTimeKind.Utc);
        var bson = new BsonDocument
        {
            { "name", "alpha" },
            { "intValue", new BsonInt32(42) },
            { "longValue", new BsonInt64(9_000_000_000L) },
            { "doubleValue", new BsonDouble(3.5) },
            { "decimalValue", new BsonDecimal128(123.45m) },
            { "dateValue", new BsonDateTime(date) },
        };

        var element = WorkItemPayloadConverter.ToJson(bson);

        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.Equal("alpha", element.GetProperty("name").GetString());

        // Numbers must be plain JSON numbers, not { "$numberInt": "..." } wrappers.
        Assert.Equal(JsonValueKind.Number, element.GetProperty("intValue").ValueKind);
        Assert.Equal(42, element.GetProperty("intValue").GetInt32());

        Assert.Equal(JsonValueKind.Number, element.GetProperty("longValue").ValueKind);
        Assert.Equal(9_000_000_000L, element.GetProperty("longValue").GetInt64());

        Assert.Equal(JsonValueKind.Number, element.GetProperty("doubleValue").ValueKind);
        Assert.Equal(3.5, element.GetProperty("doubleValue").GetDouble());

        // Decimal128 in relaxed mode is still wrapped, but it must remain a string-tagged object,
        // not collapse to a plain number that loses precision. Assert it parses back.
        var decimalProp = element.GetProperty("decimalValue");
        Assert.Equal(JsonValueKind.Object, decimalProp.ValueKind);
        Assert.Equal("123.45", decimalProp.GetProperty("$numberDecimal").GetString());

        // Dates must be emitted as { "$date": "ISO-8601" } in relaxed mode.
        var dateProp = element.GetProperty("dateValue");
        Assert.Equal(JsonValueKind.Object, dateProp.ValueKind);
        var dateString = dateProp.GetProperty("$date").GetString();
        Assert.NotNull(dateString);
        Assert.Equal(date, DateTime.Parse(dateString!, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal));
    }

    [Fact]
    public void ToBson_then_ToJson_preserves_scalar_field_shapes()
    {
        const string sourceJson = """
            {
              "name": "alpha",
              "intValue": 42,
              "doubleValue": 3.5
            }
            """;

        using var inputDoc = JsonDocument.Parse(sourceJson);
        var bson = WorkItemPayloadConverter.ToBson(inputDoc.RootElement);
        var element = WorkItemPayloadConverter.ToJson(bson);

        Assert.Equal("alpha", element.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Number, element.GetProperty("intValue").ValueKind);
        Assert.Equal(42, element.GetProperty("intValue").GetInt32());
        Assert.Equal(JsonValueKind.Number, element.GetProperty("doubleValue").ValueKind);
        Assert.Equal(3.5, element.GetProperty("doubleValue").GetDouble());
    }

    [Fact]
    public void ToBson_then_ToJson_preserves_deeply_nested_keys_no_model_declares()
    {
        // RA-292: the new-ORS, new-interim-site and authority-to-issue flags
        // reach the case management frontend as ordinary payload keys. No C#
        // type declares them — they must survive on the strength of the payload
        // being schemaless all the way through. This pins the converter half of
        // that guarantee at its deepest point: a boolean three levels down
        // inside an array element's nested object.
        const string sourceJson = """
            {
              "overseasSites": {
                "sites": [
                  {
                    "siteId": 1,
                    "isNewSite": true,
                    "repatriatedLoads": "3",
                    "isEu": true,
                    "interimSite": { "siteNumber": "INT-001", "isNewSite": true }
                  },
                  {
                    "siteId": 2,
                    "isNewSite": false,
                    "interimSite": { "siteNumber": "INT-002", "isNewSite": false }
                  },
                  { "siteId": 3 }
                ]
              },
              "prns": {
                "authorisers": [
                  { "fullName": "Grace Adeyemi", "isNew": true },
                  { "fullName": "Martin Cole", "isNew": false },
                  { "fullName": "Priya Nair" }
                ]
              }
            }
            """;

        using var inputDoc = JsonDocument.Parse(sourceJson);
        var element = WorkItemPayloadConverter.ToJson(
            WorkItemPayloadConverter.ToBson(inputDoc.RootElement));

        var sites = element.GetProperty("overseasSites").GetProperty("sites");
        Assert.Equal(3, sites.GetArrayLength());

        var newSite = sites[0];
        Assert.Equal(JsonValueKind.True, newSite.GetProperty("isNewSite").ValueKind);
        Assert.Equal(JsonValueKind.True, newSite.GetProperty("isEu").ValueKind);
        // Mixed primitives in one object: repatriatedLoads is a string at the
        // producer while siteId is a number, and both must keep their kind.
        Assert.Equal(JsonValueKind.String, newSite.GetProperty("repatriatedLoads").ValueKind);
        Assert.Equal("3", newSite.GetProperty("repatriatedLoads").GetString());
        Assert.Equal(JsonValueKind.Number, newSite.GetProperty("siteId").ValueKind);
        Assert.Equal(1, newSite.GetProperty("siteId").GetInt32());
        Assert.Equal(
            JsonValueKind.True,
            newSite.GetProperty("interimSite").GetProperty("isNewSite").ValueKind);
        Assert.Equal(
            "INT-001",
            newSite.GetProperty("interimSite").GetProperty("siteNumber").GetString());

        Assert.Equal(JsonValueKind.False, sites[1].GetProperty("isNewSite").ValueKind);
        Assert.Equal(
            JsonValueKind.False,
            sites[1].GetProperty("interimSite").GetProperty("isNewSite").ValueKind);

        // Absent stays absent — it must not be materialised as null or false.
        Assert.False(sites[2].TryGetProperty("isNewSite", out _));
        Assert.False(sites[2].TryGetProperty("interimSite", out _));

        var authorisers = element.GetProperty("prns").GetProperty("authorisers");
        Assert.Equal(3, authorisers.GetArrayLength());
        Assert.Equal(JsonValueKind.True, authorisers[0].GetProperty("isNew").ValueKind);
        Assert.Equal(JsonValueKind.False, authorisers[1].GetProperty("isNew").ValueKind);
        Assert.False(authorisers[2].TryGetProperty("isNew", out _));
    }
}
