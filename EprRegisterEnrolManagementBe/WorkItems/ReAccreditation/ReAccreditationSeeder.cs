using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Populates a fresh database with a small set of re-accreditation work
/// items so the case management UI has something to play with on first
/// boot. Only runs when the work item collection is empty (gated by
/// <see cref="WorkItemSeederHostedService"/>) so it is safe to leave
/// enabled in any environment.
///
/// Assignee ids match the stub-auth users in the frontend
/// (<c>stub-standard-1</c>, <c>stub-assign-1</c>) so the "My work items"
/// filter works immediately after a stub login.
/// </summary>
internal sealed class ReAccreditationSeeder : IWorkItemSeeder
{
    public string TypeId => ReAccreditationType.Id;

    public IEnumerable<WorkItem> Build(IWorkItemType type, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(time);

        var now = time.GetUtcNow().UtcDateTime;

        // Newly submitted, no one has picked it up yet.
        yield return Build(
            submittedDaysAgo: 1,
            stateId: "submitted",
            payload: new BsonDocument
            {
                ["organisationName"] = "Acme Recycling Ltd",
                ["registrationNumber"] = "EPR-100023",
                ["materialsHandled"] = new BsonArray { "plastic", "glass" },
                ["previousAccreditationYear"] = 2025,
                ["complianceIssuesReported"] = 0
            },
            submittedBy: "stub-portal-client",
            now: now);

        // Submitted and self-claimed by a standard user; first state still
        // has work to do.
        yield return Build(
            submittedDaysAgo: 3,
            stateId: "submitted",
            payload: new BsonDocument
            {
                ["organisationName"] = "Northern Plastics Co-op",
                ["registrationNumber"] = "EPR-100087",
                ["materialsHandled"] = new BsonArray { "plastic" },
                ["previousAccreditationYear"] = 2025,
                ["complianceIssuesReported"] = 1
            },
            submittedBy: "stub-portal-client",
            assignedToId: "stub-standard-1",
            assignedToName: "Stub Standard User",
            now: now);

        // Mid-assessment: first-state tasks complete, item has moved on,
        // and the assigner has picked up two of the three assessment
        // tasks.
        yield return Build(
            submittedDaysAgo: 9,
            stateId: "assessment-in-progress",
            payload: new BsonDocument
            {
                ["organisationName"] = "Riverside Glass Recovery"
                    ,
                ["registrationNumber"] = "EPR-099812",
                ["materialsHandled"] = new BsonArray { "glass", "metal" },
                ["previousAccreditationYear"] = 2024,
                ["complianceIssuesReported"] = 2
            },
            submittedBy: "stub-portal-client",
            assignedToId: "stub-assign-1",
            assignedToName: "Stub Assign User",
            now: now,
            completedTasks: new()
            {
                ["submitted"] =
                [
                    "verify-organisation-details",
                    "confirm-registration-fee-paid"
                ],
                ["assessment-in-progress"] =
                [
                    "review-compliance-history",
                    "assess-technical-capacity"
                ]
            });

        // Awaiting decision: every prior task complete, item parked with
        // the assigner pending the rationale being recorded.
        yield return Build(
            submittedDaysAgo: 15,
            stateId: "awaiting-decision",
            payload: new BsonDocument
            {
                ["organisationName"] = "Coastal Materials Group",
                ["registrationNumber"] = "EPR-098774",
                ["materialsHandled"] = new BsonArray { "plastic", "paper", "card" },
                ["previousAccreditationYear"] = 2024,
                ["complianceIssuesReported"] = 0
            },
            submittedBy: "stub-portal-client",
            assignedToId: "stub-assign-1",
            assignedToName: "Stub Assign User",
            now: now,
            completedTasks: new()
            {
                ["submitted"] =
                [
                    "verify-organisation-details",
                    "confirm-registration-fee-paid"
                ],
                ["assessment-in-progress"] =
                [
                    "review-compliance-history",
                    "assess-technical-capacity",
                    "assess-financial-capacity"
                ]
            });

        // Already approved — terminal state, useful for exercising the
        // "no further actions" rendering path.
        yield return Build(
            submittedDaysAgo: 32,
            stateId: "approved",
            payload: new BsonDocument
            {
                ["organisationName"] = "Heritage Paper Mills",
                ["registrationNumber"] = "EPR-097215",
                ["materialsHandled"] = new BsonArray { "paper", "card" },
                ["previousAccreditationYear"] = 2024,
                ["complianceIssuesReported"] = 0
            },
            submittedBy: "stub-portal-client",
            assignedToId: "stub-assign-1",
            assignedToName: "Stub Assign User",
            now: now,
            completedTasks: new()
            {
                ["submitted"] =
                [
                    "verify-organisation-details",
                    "confirm-registration-fee-paid"
                ],
                ["assessment-in-progress"] =
                [
                    "review-compliance-history",
                    "assess-technical-capacity",
                    "assess-financial-capacity"
                ],
                ["awaiting-decision"] =
                [
                    "record-decision-rationale"
                ]
            });
    }

    private static WorkItem Build(
        int submittedDaysAgo,
        string stateId,
        BsonDocument payload,
        string submittedBy,
        DateTime now,
        string? assignedToId = null,
        string? assignedToName = null,
        Dictionary<string, List<string>>? completedTasks = null)
    {
        var submittedAt = now.AddDays(-submittedDaysAgo);
        var assignedAt = assignedToId is null ? (DateTime?)null : submittedAt.AddHours(2);
        var lastModifiedAt = assignedAt
            ?? (completedTasks is null ? submittedAt : submittedAt.AddHours(1));

        var workItem = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            SubmittedAt = submittedAt,
            LastModifiedAt = lastModifiedAt,
            SubmittedBy = submittedBy,
            AssignedToId = assignedToId,
            AssignedToName = assignedToName,
            AssignedAt = assignedAt,
            AssignedBy = assignedToId is null ? null : assignedToId,
            Payload = payload
        };

        if (completedTasks is not null)
        {
            foreach (var (state, tasks) in completedTasks)
            {
                workItem.CompletedTaskIdsByState[state] = [.. tasks];
            }
        }

        return workItem;
    }
}