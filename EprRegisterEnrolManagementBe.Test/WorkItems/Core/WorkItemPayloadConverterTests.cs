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
}
