using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation.Models;

/// <summary>
/// RA-307: ReAccreditationPayload.GlassRecyclingProcess is deserialised via two
/// different paths in production — directly from BSON
/// (ReAccreditationApprovalService, ReAccreditationDulyMakingService, the
/// notification/query hooks) and via System.Text.Json after
/// WorkItemPayloadConverter.ToJson (ReAccreditationEndpoints, serving the
/// case-management frontend). Both must round-trip the wire values
/// glass_re_melt/glass_other correctly, since the enum's member names are
/// deliberately spelled to match them — see GlassRecyclingProcess.cs.
/// </summary>
public class ReAccreditationPayloadGlassRecyclingProcessTests
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Theory]
    [InlineData("glass_re_melt")]
    [InlineData("glass_other")]
    public void BsonDeserialize_RecognisedWireValue_MapsToEnum(string rawValue)
    {
        var document = new BsonDocument { { "glassRecyclingProcess", rawValue } };

        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(document);

        // The enum's member names are deliberately spelled to match the wire
        // value verbatim (see GlassRecyclingProcess.cs), so ToString() round
        // trips it — this also proves the deserialised enum is not just "some
        // truthy value" but the exact member the wire value names.
        Assert.Equal(rawValue, payload.GlassRecyclingProcess?.ToString());
    }

    [Fact]
    public void BsonDeserialize_FieldAbsent_MapsToNull()
    {
        // Every non-glass application, and every glass application predating
        // RA-307, has no glassRecyclingProcess key in the stored payload at all.
        var document = new BsonDocument { { "material", "steel" } };

        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(document);

        Assert.Null(payload.GlassRecyclingProcess);
    }

    [Fact]
    public void BsonDeserialize_UnrecognisedWireValue_ThrowsFormatException()
    {
        var document = new BsonDocument { { "glassRecyclingProcess", "glass_pulverise" } };

        // Every raw-BSON call site (ReAccreditationApprovalService,
        // ReAccreditationDulyMakingService, ReAccreditationNotificationHook,
        // ReAccreditationQueryPushHook) already wraps this deserialisation in a
        // try/catch that treats FormatException as a recoverable payload-shape
        // error rather than an unhandled 500 — this test documents that an
        // unrecognised value takes that existing path rather than silently
        // producing a wrong enum value.
        Assert.Throws<FormatException>(() =>
            BsonSerializer.Deserialize<ReAccreditationPayload>(document)
        );
    }

    [Theory]
    [InlineData("glass_re_melt")]
    [InlineData("glass_other")]
    public void JsonDeserialize_RecognisedWireValue_MapsToEnum(string rawValue)
    {
        var document = new BsonDocument { { "glassRecyclingProcess", rawValue } };
        var payloadJson = WorkItemPayloadConverter.ToJson(document);

        var payload = payloadJson.Deserialize<ReAccreditationPayload>(PayloadJsonOptions);

        Assert.Equal(rawValue, payload!.GlassRecyclingProcess?.ToString());
    }

    [Fact]
    public void JsonDeserialize_FieldAbsent_MapsToNull()
    {
        var document = new BsonDocument { { "material", "steel" } };
        var payloadJson = WorkItemPayloadConverter.ToJson(document);

        var payload = payloadJson.Deserialize<ReAccreditationPayload>(PayloadJsonOptions);

        Assert.Null(payload!.GlassRecyclingProcess);
    }

    [Fact]
    public void BsonRoundTrip_PreservesGlassRecyclingProcess()
    {
        var payload = new ReAccreditationPayload
        {
            Material = "glass",
            GlassRecyclingProcess = GlassRecyclingProcess.glass_re_melt,
        };

        var document = payload.ToBsonDocument();
        var roundTripped = BsonSerializer.Deserialize<ReAccreditationPayload>(document);

        Assert.Equal(GlassRecyclingProcess.glass_re_melt, roundTripped.GlassRecyclingProcess);
        Assert.Equal("glass_re_melt", document["glassRecyclingProcess"].AsString);
    }
}
