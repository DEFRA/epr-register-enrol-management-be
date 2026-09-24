using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// Reads <c>payload.siteAddress</c> as a single line for the SubmissionConfirmation
/// email, whichever of its two stored shapes it arrives in (the same two shapes
/// management-fe's <c>formatSiteAddress</c> normalises):
/// <list type="bullet">
///   <item>a flat string — sent by epr-register-enrol-backend at submission;</item>
///   <item>a nested document <c>{ line1, line2, town, postcode }</c> — stored for
///   work items created through the case management form. Rendered as
///   "line1, line2, town" (the postcode has its own field), matching the frontend.</item>
/// </list>
/// Deliberately a read-only helper over the raw payload rather than a property on
/// <see cref="ReAccreditationPayload"/>. That record is round-tripped
/// (deserialise → <c>with</c> → <c>ToBsonDocument()</c> → merge over the stored
/// document with <c>overwriteExistingElements</c>) on duly-making and approval, so
/// modelling <c>siteAddress</c> would re-emit it and overwrite the stored nested
/// document — destroying the only copy of a form-created item's postcode.
/// Leaving it unmodelled keeps that cycle from ever touching it.
/// </summary>
public static class SiteAddressFormatter
{
    private static readonly string[] s_addressLineFields = ["line1", "line2", "town"];

    /// <summary>
    /// The single-line address, or <c>null</c> when the payload has none in a usable shape.
    /// </summary>
    public static string? Format(BsonDocument? payload)
    {
        if (payload is null || !payload.TryGetValue("siteAddress", out var siteAddress))
        {
            return null;
        }

        if (siteAddress.IsString)
        {
            return string.IsNullOrWhiteSpace(siteAddress.AsString) ? null : siteAddress.AsString.Trim();
        }

        if (!siteAddress.IsBsonDocument)
        {
            return null;
        }

        var document = siteAddress.AsBsonDocument;
        var parts = s_addressLineFields
            .Select(field => document.TryGetValue(field, out var value) && value.IsString
                ? value.AsString.Trim()
                : string.Empty)
            .Where(part => part.Length > 0)
            .ToList();
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }
}
