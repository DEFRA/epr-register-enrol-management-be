using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;

public sealed class AddOverseasSiteType : IWorkItemType, IWorkItemSubmissionValidator
{
    public string TypeId => "add-overseas-site";
    public string DisplayName => "Add ORS";
    public string TemplateVersion => "v1";
    public WorkItemState InitialState => States.First();
    public IReadOnlyCollection<WorkItemState> States { get; } =
    [
        new("submitted", "Submitted"),
        new("assessment-in-progress", "Assessment in progress"),
        new("approved", "Granted", true),
        new("rejected", "Refused", true),
        new("withdrawn", "Withdrawn", true),
    ];
    // Module services will own these operations and their cross-service effects.
    public IReadOnlyCollection<WorkItemTransition> Transitions { get; } =
    [
        new("start-assessment", "Start assessment", "submitted", "assessment-in-progress", false),
        new("approve", "Approve", "assessment-in-progress", "approved", false),
        new("reject", "Refuse", "assessment-in-progress", "rejected", false),
        new("withdraw", "Withdraw", "submitted", "withdrawn", false),
    ];

    public string? ValidateSubmission(BsonDocument payload)
    {
        if (!Guid.TryParse(payload.GetValue("operatorRequestId", "").ToString(), out var requestId) || requestId == Guid.Empty
            || !Guid.TryParse(payload.GetValue("proposedSiteId", "").ToString(), out var siteId) || siteId == Guid.Empty
            || payload.GetValue("schemaVersion", 0) != 1 || payload.Contains("operatorApplicationId")
            || !payload.TryGetValue("target", out var target) || !target.IsBsonDocument
            || !payload.TryGetValue("proposal", out var proposal) || !proposal.IsBsonDocument
            || !payload.TryGetValue("baselineSites", out var baseline) || !baseline.IsBsonArray)
            return "A version 1 change request, target, baseline and stable proposed-site identity are required.";
        foreach (var field in new[] { "organisationId", "registrationId", "accreditationId", "materialType" })
            if (!target.AsBsonDocument.TryGetValue(field, out var value) || !value.IsString || string.IsNullOrWhiteSpace(value.AsString))
                return "The exact accreditation target is required.";
        if (!target.AsBsonDocument.TryGetValue("year", out var year) || !year.IsInt32 || year.AsInt32 <= 0)
            return "The target year is required.";
        foreach (var field in new[] { "siteName", "siteAddress", "country" })
            if (!proposal.AsBsonDocument.TryGetValue(field, out var value) || !value.IsString || string.IsNullOrWhiteSpace(value.AsString))
                return "The prepared overseas-site proposal is required.";
        return null;
    }

    public string? ValidateReplay(BsonDocument payload, BsonDocument storedPayload)
    {
        foreach (var field in new[] { "schemaVersion", "operatorRequestId", "target", "proposedSiteId", "proposal", "baselineSites" })
            if (!payload.GetValue(field, BsonNull.Value).Equals(storedPayload.GetValue(field, BsonNull.Value)))
                return "A repeated submission must retain the original request, target and proposal.";
        return null;
    }
}
