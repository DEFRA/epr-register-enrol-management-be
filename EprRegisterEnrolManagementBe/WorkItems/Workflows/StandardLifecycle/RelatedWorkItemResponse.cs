using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-495: a work item from the same submission, as the Duly make page's
/// related change requests and (RA-770) the Related change requests tab show
/// it. The site fields are null for a work item with no <c>payload.site</c>;
/// the charge is null when its type has not opted in to the Standard lifecycle
/// or the work item carries none. RA-681: <c>Selected</c> is the site's
/// <c>selected</c> flag, which management-fe uses to label an ORS update
/// "Accreditation:" or "Registration:"; null when the site has none.
/// </summary>
internal sealed record RelatedWorkItemResponse(
    Guid Id,
    string TypeId,
    string StateId,
    string? ApplicationReference,
    string? SiteName,
    string? OrsId,
    bool? Selected,
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
            BoolOrNull(site, "selected"),
            strategy?.ReadChargePence(workItem)
        );
    }

    private static string? StringOrNull(BsonDocument? document, string field) =>
        document is not null && document.TryGetValue(field, out var value) && value.IsString ? value.AsString : null;

    private static bool? BoolOrNull(BsonDocument? document, string field) =>
        document is not null && document.TryGetValue(field, out var value) && value.IsBoolean ? value.AsBoolean : null;
}
