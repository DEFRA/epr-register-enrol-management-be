using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-495: a work item from the same submission, as the Duly make page's
/// related change requests and (RA-770) the Related change requests tab show
/// it. The site fields are null for a work item with no <c>payload.site</c>;
/// the charge is null when its type has not opted in to the Standard lifecycle
/// or the work item carries none.
/// </summary>
internal sealed record RelatedWorkItemResponse(
    Guid Id,
    string TypeId,
    string StateId,
    string? ApplicationReference,
    string? SiteName,
    string? OrsId,
    int? ChargePence
)
{
    public static RelatedWorkItemResponse From(WorkItem workItem, IStandardLifecycleStrategy? strategy)
    {
        var payload = workItem.Payload;
        var site = SubmissionPayload.ReadSite(payload);
        return new RelatedWorkItemResponse(
            workItem.Id,
            workItem.TypeId,
            workItem.StateId,
            StringOrNull(payload, "applicationReference"),
            StringOrNull(site, "siteName"),
            StringOrNull(site, "orsId"),
            strategy?.ReadChargePence(workItem)
        );
    }

    private static string? StringOrNull(BsonDocument? document, string field) =>
        document is not null && document.TryGetValue(field, out var value) && value.IsString ? value.AsString : null;
}
