using System.Globalization;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-739: the fields a submission for a type that declares
/// <see cref="IWorkItemType.SplitsSubmissionPerSite"/> (add-ORS) must carry.
/// <see cref="WorkItemService.SubmitPerSiteAsync"/> rejects a payload that
/// fails <see cref="Validate"/> with a 400 before anything is allocated or
/// written. Every other field is optional.
/// </summary>
internal static class SubmissionPayload
{
    /// <summary>The operator's id for the request: the replay key and the submission's <c>_id</c>.</summary>
    public const string OperatorRequestIdField = "operatorRequestId";

    public const string SitesField = "sites";
    public const string SiteIdField = "siteId";

    /// <summary>
    /// RA-739: stamped on every work item a submission creates, so the work
    /// items of one per-site submission share it.
    /// </summary>
    public const string SubmissionReferenceField = "submissionReference";

    /// <summary>
    /// RA-739: on each work item a per-site submission creates, that work
    /// item's own entry from <see cref="SitesField"/>.
    /// </summary>
    public const string SiteField = "site";

    /// <summary>
    /// RA-739: stamped on every work item a submission creates: the number of
    /// applications in the operator's submission.
    /// </summary>
    public const string SubmissionSizeField = "submissionSize";

    public const string OperatorOrganisationIdField = "operatorOrganisationId";

    /// <summary>
    /// RA-767: on a site, the split-per-site type its work item is created as,
    /// so one request can hold adds and updates. Optional: a site without one
    /// takes the request's <c>typeId</c>.
    /// </summary>
    public const string SiteTypeIdField = "typeId";

    /// <summary>
    /// RA-767: <c>errorCode</c> for a site <see cref="SiteTypeIdField"/> that
    /// is not a registered type that splits a submission per site.
    /// </summary>
    public const string UnknownSiteTypeErrorCode = "unknown-site-type";

    /// <summary>
    /// RA-767: <c>errorCode</c> for a site <see cref="SiteTypeIdField"/> whose
    /// type is not in the request type's category.
    /// </summary>
    public const string SiteTypeMismatchErrorCode = "site-type-mismatch";

    /// <summary>A site type that cannot be used: the message and the wire <c>errorCode</c>.</summary>
    public sealed record SiteTypeError(string Message, string ErrorCode);

    /// <summary>RA-682: required by every type that charges (see <see cref="ChargedRequiredFields"/>).</summary>
    public const string PaymentReferenceField = "paymentReference";

    /// <summary>
    /// RA-682: the fields a submission must carry when its type is never
    /// charged (the ORS update), so it has no <see cref="PaymentReferenceField"/>.
    /// </summary>
    public static readonly IReadOnlyCollection<string> UnchargedRequiredFields =
    [
        OperatorRequestIdField,
        OperatorOrganisationIdField,
        "organisationName",
        "material",
    ];

    /// <summary>
    /// The fields a submission must carry when its type charges: the
    /// <see cref="UnchargedRequiredFields"/> and <see cref="PaymentReferenceField"/>.
    /// The default for <see cref="IWorkItemType.RequiredSubmissionFields"/>.
    /// </summary>
    public static readonly IReadOnlyCollection<string> ChargedRequiredFields =
    [
        .. UnchargedRequiredFields,
        PaymentReferenceField,
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

    /// <summary>
    /// RA-767: the type each of the payload's sites is created as, in site
    /// order: its <see cref="SiteTypeIdField"/>, or
    /// <paramref name="requestType"/> when it has none. A site type must be
    /// registered (<paramref name="findType"/>), split a submission per site,
    /// and be <paramref name="requestType"/> or share its non-null
    /// <see cref="IWorkItemType.Category"/>; otherwise <paramref name="error"/>
    /// says why. A payload with no usable <see cref="SitesField"/>, or a site
    /// that is not an object, is left to <see cref="Validate"/>.
    /// </summary>
    public static IReadOnlyList<IWorkItemType> ResolveSiteTypes(
        BsonDocument payload,
        IWorkItemType requestType,
        Func<string, IWorkItemType?> findType,
        out SiteTypeError? error
    )
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(findType);

        error = null;
        if (!payload.TryGetValue(SitesField, out var sitesValue) || !sitesValue.IsBsonArray)
        {
            return [];
        }

        var types = new List<IWorkItemType>(sitesValue.AsBsonArray.Count);
        foreach (var (siteValue, index) in sitesValue.AsBsonArray.Select((site, i) => (site, i)))
        {
            if (!siteValue.IsBsonDocument || !siteValue.AsBsonDocument.TryGetValue(SiteTypeIdField, out var typeIdValue))
            {
                types.Add(requestType);
                continue;
            }

            error = CheckSiteType(typeIdValue, index, requestType, findType, out var siteType);
            if (error is not null)
            {
                return [];
            }
            types.Add(siteType!);
        }

        return types;
    }

    private static SiteTypeError? CheckSiteType(
        BsonValue typeIdValue,
        int index,
        IWorkItemType requestType,
        Func<string, IWorkItemType?> findType,
        out IWorkItemType? siteType
    )
    {
        var path = $"payload.{SitesField}[{index}].{SiteTypeIdField}";
        siteType =
            typeIdValue.IsString && !string.IsNullOrWhiteSpace(typeIdValue.AsString)
                ? findType(typeIdValue.AsString)
                : null;
        if (siteType is not { SplitsSubmissionPerSite: true })
        {
            siteType = null;
            return new SiteTypeError(
                $"{path} must be a registered work item type that splits a submission per site.",
                UnknownSiteTypeErrorCode
            );
        }

        var sameGroup =
            string.Equals(siteType.TypeId, requestType.TypeId, StringComparison.Ordinal)
            || (requestType.Category is not null && siteType.Category == requestType.Category);
        if (sameGroup)
        {
            return null;
        }

        var mismatched = siteType.TypeId;
        siteType = null;
        return new SiteTypeError(
            $"{path} '{mismatched}' is not in the same category as the request's type '{requestType.TypeId}'.",
            SiteTypeMismatchErrorCode
        );
    }

    /// <summary>The work item payload's <see cref="SubmissionReferenceField"/>, when it is a non-blank string.</summary>
    public static bool TryReadSubmissionReference(BsonDocument? payload, out string submissionReference)
    {
        submissionReference =
            payload is not null && IsPresentString(payload, SubmissionReferenceField)
                ? payload[SubmissionReferenceField].AsString
                : string.Empty;
        return submissionReference.Length > 0;
    }

    /// <summary>The work item payload's own <see cref="SiteField"/>, or <c>null</c> when it has none.</summary>
    public static BsonDocument? ReadSite(BsonDocument? payload) =>
        payload is not null && payload.TryGetValue(SiteField, out var site) && site.IsBsonDocument
            ? site.AsBsonDocument
            : null;

    /// <summary>
    /// Why <paramref name="payload"/> cannot be submitted, or <c>null</c> when it can.
    /// RA-682: <paramref name="requiredFields"/> are the type's
    /// <see cref="IWorkItemType.RequiredSubmissionFields"/>, each of which must
    /// be a non-blank string.
    /// </summary>
    public static string? Validate(BsonDocument payload, IEnumerable<string> requiredFields)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(requiredFields);

        if (requiredFields.FirstOrDefault(field => !IsPresentString(payload, field)) is { } missing)
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
