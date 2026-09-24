using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// Reads <c>payload.siteAddress</c> into a single-line string whichever of its
/// two stored shapes it arrives in (the same two shapes management-fe's
/// <c>formatSiteAddress</c> normalises):
/// <list type="bullet">
///   <item>a flat string — sent by epr-register-enrol-backend at submission;</item>
///   <item>a nested document <c>{ line1, line2, town, postcode }</c> — stored for
///   work items created through the case management form. Rendered as
///   "line1, line2, town" (postcode has its own field), matching the frontend.</item>
/// </list>
/// A plain string property would throw on the nested shape and take down
/// deserialisation of the whole payload, so every hook and service that reads
/// the payload would fail for form-created items.
/// </summary>
public sealed class SiteAddressBsonSerializer : SerializerBase<string?>
{
    private static readonly string[] s_addressLineFields = ["line1", "line2", "town"];

    public override string? Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        switch (reader.CurrentBsonType)
        {
            case BsonType.String:
                return Blank(reader.ReadString());

            case BsonType.Document:
                var document = BsonDocumentSerializer.Instance.Deserialize(context);
                var parts = s_addressLineFields
                    .Select(field => document.TryGetValue(field, out var value) && value.IsString
                        ? value.AsString.Trim()
                        : string.Empty)
                    .Where(part => part.Length > 0)
                    .ToList();
                return parts.Count > 0 ? string.Join(", ", parts) : null;

            case BsonType.Null:
                reader.ReadNull();
                return null;

            default:
                reader.SkipValue();
                return null;
        }
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, string? value)
    {
        if (value is null)
        {
            context.Writer.WriteNull();
            return;
        }

        context.Writer.WriteString(value);
    }

    private static string? Blank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
