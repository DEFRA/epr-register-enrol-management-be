using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-739: the <c>POST /work-items</c> 201 body for a type that splits a
/// submission per site (add-ORS): the submission reference every work item
/// shares, and one entry per submitted site.
/// </summary>
public sealed record WorkItemSubmissionResponse(
    string SubmissionReference,
    IReadOnlyList<WorkItemSubmissionItemResponse> WorkItems
)
{
    /// <summary>The response for a stored <paramref name="submission"/>.</summary>
    public static WorkItemSubmissionResponse From(SubmissionRecord submission) =>
        new(
            submission.SubmissionReference,
            submission.WorkItems.Select(item => WorkItemSubmissionItemResponse.From(item, submission.TypeId)).ToList()
        );
}

/// <summary>
/// RA-739: the work item created for one submitted site. <see cref="SiteId"/>
/// echoes the submitted <c>siteId</c> as sent: a number from the operator
/// backend, or a string. It is an <see cref="object"/> so System.Text.Json
/// writes it with that JSON type; <see cref="SubmissionRecordItem.SiteId"/>
/// holds the same value as a <see cref="BsonValue"/> so it is stored with it.
/// RA-767: <see cref="TypeId"/> is the work item's own type, since one request
/// can mix site types.
/// </summary>
public sealed record WorkItemSubmissionItemResponse(
    object? SiteId,
    Guid Id,
    string ApplicationReference,
    string TypeId
)
{
    /// <summary>
    /// The response for one stored <paramref name="item"/>. An item stored
    /// before RA-767 has no type of its own, so it takes
    /// <paramref name="submissionTypeId"/>.
    /// </summary>
    public static WorkItemSubmissionItemResponse From(SubmissionRecordItem item, string submissionTypeId) =>
        new(
            BsonTypeMapper.MapToDotNetValue(item.SiteId),
            item.WorkItemId,
            item.ApplicationReference,
            item.TypeId ?? submissionTypeId
        );
}
