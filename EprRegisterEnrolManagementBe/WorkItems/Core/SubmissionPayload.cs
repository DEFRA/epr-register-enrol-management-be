using System.Globalization;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-739: the fields a submission for a type that declares
/// <see cref="IWorkItemType.UsesSubmissionReference"/> (add-ORS) must carry.
/// <see cref="WorkItemService.SubmitSubmissionAsync"/> rejects a payload that
/// fails <see cref="Validate"/> with a 400 before anything is allocated or
/// written. Every other field is optional.
/// </summary>
internal static class SubmissionPayload
{
    /// <summary>The operator's id for the request: the replay key and the submission's <c>_id</c>.</summary>
    public const string OperatorRequestIdField = "operatorRequestId";

    public const string SitesField = "sites";
    public const string SiteIdField = "siteId";

    private static readonly string[] s_requiredFields =
    [
        OperatorRequestIdField,
        "operatorOrganisationId",
        "organisationName",
        "material",
        "paymentReference",
    ];

    private static readonly string[] s_requiredSiteFields = ["orsId", "siteName"];

    /// <summary>The payload's <c>operatorRequestId</c>, when it is a non-blank string.</summary>
    public static bool TryReadOperatorRequestId(BsonDocument payload, out string operatorRequestId)
    {
        operatorRequestId = IsPresentString(payload, OperatorRequestIdField)
            ? payload[OperatorRequestIdField].AsString
            : string.Empty;
        return operatorRequestId.Length > 0;
    }

    /// <summary>Why <paramref name="payload"/> cannot be submitted, or <c>null</c> when it can.</summary>
    public static string? Validate(BsonDocument payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (s_requiredFields.FirstOrDefault(field => !IsPresentString(payload, field)) is { } missing)
        {
            return $"payload.{missing} is required.";
        }

        if (!AccreditationYear.TryRead(payload, out _))
        {
            return $"payload.{AccreditationYear.Field} must be a 4-digit year.";
        }

        if (
            !payload.TryGetValue(SitesField, out var sitesValue)
            || !sitesValue.IsBsonArray
            || sitesValue.AsBsonArray.Count == 0
        )
        {
            return $"payload.{SitesField} must hold at least one site.";
        }

        var siteKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (siteValue, index) in sitesValue.AsBsonArray.Select((site, i) => (site, i)))
        {
            var path = $"payload.{SitesField}[{index}]";
            if (!siteValue.IsBsonDocument)
            {
                return $"{path} must be a site object.";
            }

            var site = siteValue.AsBsonDocument;
            if (SiteKey(site) is not { } siteKey)
            {
                return $"{path}.{SiteIdField} is required.";
            }

            if (s_requiredSiteFields.FirstOrDefault(field => !IsPresentString(site, field)) is { } missingSiteField)
            {
                return $"{path}.{missingSiteField} is required.";
            }

            if (!siteKeys.Add(siteKey))
            {
                return $"{path}.{SiteIdField} repeats an earlier site.";
            }
        }

        return null;
    }

    /// <summary>
    /// A key identifying the site's <c>siteId</c>, which the operator backend
    /// sends as a number and other callers may send as a string, or
    /// <c>null</c> when it has none. The type is part of the key, as it is in
    /// a Mongo index, so <c>1</c> and <c>"1"</c> are different sites.
    /// </summary>
    private static string? SiteKey(BsonDocument site)
    {
        if (!site.TryGetValue(SiteIdField, out var siteId))
        {
            return null;
        }

        if (siteId.IsString)
        {
            return string.IsNullOrWhiteSpace(siteId.AsString) ? null : "s:" + siteId.AsString;
        }

        return BsonWholeNumber.TryGet(siteId, int.MinValue, int.MaxValue, out var number)
            ? "n:" + number.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static bool IsPresentString(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value)
        && value.IsString
        && !string.IsNullOrWhiteSpace(value.AsString);
}
