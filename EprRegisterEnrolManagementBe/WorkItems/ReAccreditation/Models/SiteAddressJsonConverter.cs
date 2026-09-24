using System.Text.Json;
using System.Text.Json.Serialization;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// System.Text.Json counterpart of <see cref="SiteAddressBsonSerializer"/>: the
/// prior-year, resume and similar endpoints deserialise the payload from JSON
/// rather than BSON, and a plain string property would throw on the nested
/// <c>{ line1, line2, town, postcode }</c> shape of form-created work items.
/// Reads either shape into a single line and anything else as null.
/// </summary>
public sealed class SiteAddressJsonConverter : JsonConverter<string?>
{
    private static readonly string[] s_addressLineFields = ["line1", "line2", "town"];

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();

            case JsonValueKind.Object:
                var parts = s_addressLineFields
                    .Select(field => element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                        ? value.GetString()?.Trim() ?? string.Empty
                        : string.Empty)
                    .Where(part => part.Length > 0)
                    .ToList();
                return parts.Count > 0 ? string.Join(", ", parts) : null;

            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value);
    }
}
