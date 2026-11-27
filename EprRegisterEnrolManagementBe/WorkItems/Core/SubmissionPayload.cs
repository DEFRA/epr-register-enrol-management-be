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
    /// RA-739: stamped on every work item a submission creates: the number of
    /// applications in the operator's submission.
    /// </summary>
    public const string SubmissionSizeField = "submissionSize";

    /// <summary>
    /// The operator organisation the request is from. RA-767: stored on the
    /// submission record, so a later group can share an earlier group's
    /// <see cref="SubmissionReferenceField"/> only from the same organisation.
    /// </summary>
    public const string OperatorOrganisationIdField = "operatorOrganisationId";

    /// <summary>
    /// RA-767: the largest <see cref="SubmissionSizeField"/> accepted, well
    /// beyond any real operator request, so a typo cannot stamp a size that
    /// would mislead the caseworker.
    /// </summary>
    public const int MaxSubmissionSize = 1000;

    /// <summary>
    /// RA-767: separates an operator request's id from the suffix naming a
    /// later group of it, as in <c>{requestId}:update-overseas-site</c>.
    /// </summary>
    public const char RequestGroupSeparator = ':';

    /// <summary>RA-767: <c>errorCode</c> for a supplied <see cref="SubmissionReferenceField"/> that is not a non-blank string.</summary>
    public const string InvalidSubmissionReferenceErrorCode = "invalid-submission-reference";

    /// <summary>RA-767: <c>errorCode</c> for a supplied <see cref="SubmissionReferenceField"/> on a request that is not a later group.</summary>
    public const string SubmissionReferenceNotAllowedErrorCode = "submission-reference-not-allowed";

    /// <summary>RA-767: <c>errorCode</c> for a supplied <see cref="SubmissionReferenceField"/> that is not the base request's.</summary>
    public const string SubmissionReferenceNotFoundErrorCode = "submission-reference-not-found";

    /// <summary>
    /// RA-767: <c>errorCode</c> for a supplied <see cref="SubmissionSizeField"/>
    /// that is not a whole number from this POST's site count to
    /// <see cref="MaxSubmissionSize"/>, or, on a later group sharing a
    /// reference, that differs from the size stored for the request.
    /// </summary>
    public const string InvalidSubmissionSizeErrorCode = "invalid-submission-size";

    /// <summary>
    /// RA-739: on each work item a per-site submission creates, that work
    /// item's own entry from <see cref="SitesField"/>.
    /// </summary>
    public const string SiteField = "site";

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
    /// RA-767: what a per-site submission that is one group of a larger
    /// operator request says about that request: the
    /// <see cref="SubmissionReferenceField"/> an earlier group was given, and
    /// the <see cref="SubmissionSizeField"/> across every group. Either is
    /// <c>null</c> when not sent (absent or JSON null).
    /// </summary>
    public sealed record SharedSubmission(string? SubmissionReference, int? SubmissionSize);

    /// <summary>A rejected <see cref="SharedSubmission"/>: the message and the wire <c>errorCode</c>.</summary>
    public sealed record SharedSubmissionError(string Message, string ErrorCode);

    /// <summary>
    /// RA-767: why the optional <see cref="SharedSubmission"/> fields of an
    /// already <see cref="Validate">validated</see> payload cannot be used, or
    /// <c>null</c> when they can (<see cref="ReadSharedSubmission"/>). A
    /// <see cref="SubmissionReferenceField"/> is accepted only on a later group
    /// (an <see cref="OperatorRequestIdField"/> holding
    /// <see cref="RequestGroupSeparator"/>), and a
    /// <see cref="SubmissionSizeField"/> must be a whole number from this
    /// POST's site count to <see cref="MaxSubmissionSize"/>.
    /// </summary>
    public static SharedSubmissionError? ValidateSharedSubmission(BsonDocument payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (IsSent(payload, SubmissionReferenceField))
        {
            if (!IsPresentString(payload, SubmissionReferenceField))
            {
                return new SharedSubmissionError(
                    $"payload.{SubmissionReferenceField} must be a non-blank string when sent.",
                    InvalidSubmissionReferenceErrorCode
                );
            }

            TryReadOperatorRequestId(payload, out var operatorRequestId);
            if (BaseRequestId(operatorRequestId) is null)
            {
                return new SharedSubmissionError(
                    $"payload.{SubmissionReferenceField} may only be sent for a later group of an operator "
                        + $"request, whose {OperatorRequestIdField} is '{{requestId}}{RequestGroupSeparator}{{group}}'.",
                    SubmissionReferenceNotAllowedErrorCode
                );
            }
        }

        var siteCount = payload[SitesField].AsBsonArray.Count;
        if (
            IsSent(payload, SubmissionSizeField)
            && !BsonWholeNumber.TryGet(payload[SubmissionSizeField], siteCount, MaxSubmissionSize, out _)
        )
        {
            return new SharedSubmissionError(
                $"payload.{SubmissionSizeField} must be a whole number from {siteCount}, "
                    + $"the number of sites in this submission, to {MaxSubmissionSize}.",
                InvalidSubmissionSizeErrorCode
            );
        }

        return null;
    }

    /// <summary>
    /// RA-767: the <see cref="SharedSubmission"/> fields of a payload that
    /// passed <see cref="ValidateSharedSubmission"/>.
    /// </summary>
    public static SharedSubmission ReadSharedSubmission(BsonDocument payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var reference = IsPresentString(payload, SubmissionReferenceField)
            ? payload[SubmissionReferenceField].AsString
            : null;
        int? size = IsSent(payload, SubmissionSizeField)
            && BsonWholeNumber.TryGet(payload[SubmissionSizeField], 1, MaxSubmissionSize, out var value)
            ? value
            : null;
        return new SharedSubmission(reference, size);
    }

    /// <summary>
    /// RA-767: the operator request a later group belongs to, the
    /// <paramref name="operatorRequestId"/> up to the first
    /// <see cref="RequestGroupSeparator"/>, or <c>null</c> when it has none.
    /// </summary>
    public static string? BaseRequestId(string operatorRequestId)
    {
        var separator = operatorRequestId.IndexOf(RequestGroupSeparator, StringComparison.Ordinal);
        return separator < 0 ? null : operatorRequestId[..separator];
    }

    /// <summary>The payload's <see cref="OperatorOrganisationIdField"/>, or <c>null</c> when it is not a non-blank string.</summary>
    public static string? ReadOperatorOrganisationId(BsonDocument payload) =>
        IsPresentString(payload, OperatorOrganisationIdField)
            ? payload[OperatorOrganisationIdField].AsString
            : null;

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

    private static bool IsSent(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && !value.IsBsonNull;

    private static bool IsPresentString(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value)
        && value.IsString
        && !string.IsNullOrWhiteSpace(value.AsString);
}
