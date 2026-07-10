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
///
/// RA-175: <see cref="INationResolver"/> is injected so the seeder calls
/// the same postcode-to-nation routing logic that
/// <see cref="ReAccreditationNationRoutingHook"/> applies to real
/// submissions. This ensures seeded items carry a correctly-derived
/// <c>payload.nation</c> value and appear under the right nation filter
/// in the work queue.
/// </summary>
internal sealed class ReAccreditationSeeder(INationResolver nationResolver) : IWorkItemSeeder
{
    /// <summary>
    /// Sentinel <see cref="WorkItem.AssignedBy"/> value attributed to the
    /// seeder. Distinct from any real user id so the audit log makes the
    /// provenance of seeded assignments explicit and queryable. Setting
    /// <c>AssignedBy</c> to the assignee id (the original bug, epr-ce4)
    /// would falsify the audit trail to claim the assignee assigned
    /// themselves.
    /// </summary>
    public const string SeederAssignedBy = "system:seeder";

    public string TypeId => ReAccreditationType.Id;

    public IEnumerable<WorkItem> Build(IWorkItemType type, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(time);

        var now = time.GetUtcNow().UtcDateTime;

        // Newly submitted, no one has picked it up yet.
        yield return Build(
            seedKey: "acme-recycling",
            postcode: "SW1A 1AA",
            submittedDaysAgo: 1,
            stateId: "submitted",
            payload: new BsonDocument
            {
                ["organisationName"] = "Acme Recycling Ltd",
                ["registrationNumber"] = "EPR-100023",
                ["operatorRegistrationId"] = "reg-001",
                ["material"] = "plastic",
                ["previousAccreditationYear"] = 2025,
                ["complianceIssuesReported"] = 0,
                ["operatorEmail"] = "acme.recycling@example.com",
                ["siteAddressPostcode"] = "SW1A 1AA"
            },
            submittedBy: "stub-portal-client",
            now: now);

        // Submitted and self-claimed by a standard user; first state still
        // has work to do.
        yield return Build(
            seedKey: "northern-plastics",
            postcode: "EH1 3BN",
            submittedDaysAgo: 3,
            stateId: "submitted",
            payload: new BsonDocument
            {
                ["organisationName"] = "Northern Plastics Co-op",
                ["registrationNumber"] = "EPR-100087",
                ["operatorRegistrationId"] = "reg-002",
                ["material"] = "plastic",
                ["previousAccreditationYear"] = 2025,
                ["complianceIssuesReported"] = 1,
                ["operatorEmail"] = "northern.plastics@example.com",
                ["siteAddressPostcode"] = "EH1 3BN"
            },
            submittedBy: "stub-portal-client",
            assignedToId: "stub-standard-1",
            assignedToName: "Stub Standard User",
            now: now);

        // Mid-assessment: first-state tasks complete, item has moved on,
        // and the assigner has picked up two of the three assessment
        // tasks.
        yield return Build(
            seedKey: "riverside-glass",
            postcode: "CF10 1AA",
            submittedDaysAgo: 9,
            stateId: "assessment-in-progress",
            payload: new BsonDocument
            {
                ["organisationName"] = "Riverside Glass Recovery",
                ["registrationNumber"] = "EPR-099812",
                ["operatorRegistrationId"] = "reg-003",
                ["material"] = "glass",
                ["previousAccreditationYear"] = 2024,
                ["complianceIssuesReported"] = 2,
                ["operatorEmail"] = "riverside.glass@example.com",
                ["siteAddressPostcode"] = "CF10 1AA"
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
                    "confirm-application-completeness"
                ],
                ["duly-made"] =
                [
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
            seedKey: "coastal-materials",
            postcode: "BT1 1AA",
            submittedDaysAgo: 15,
            stateId: "awaiting-decision",
            payload: new BsonDocument
            {
                ["organisationName"] = "Coastal Materials Group",
                ["registrationNumber"] = "EPR-098774",
                ["operatorRegistrationId"] = "reg-004",
                ["material"] = "plastic",
                ["previousAccreditationYear"] = 2024,
                ["complianceIssuesReported"] = 0,
                ["operatorEmail"] = "coastal.materials@example.com",
                ["siteAddressPostcode"] = "BT1 1AA"
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
                    "confirm-application-completeness"
                ],
                ["duly-made"] =
                [
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
            seedKey: "heritage-paper",
            postcode: "BS1 4DJ",
            submittedDaysAgo: 32,
            stateId: "approved",
            payload: new BsonDocument
            {
                ["organisationName"] = "Heritage Paper Mills",
                ["registrationNumber"] = "EPR-097215",
                ["operatorRegistrationId"] = "reg-005",
                ["material"] = "paper",
                ["previousAccreditationYear"] = 2024,
                ["complianceIssuesReported"] = 0,
                ["operatorEmail"] = "heritage.paper@example.com",
                ["siteAddressPostcode"] = "BS1 4DJ"
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
                    "confirm-application-completeness"
                ],
                ["duly-made"] =
                [
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

        // Additional Scotland item — submitted, unassigned.
        yield return Build(
            seedKey: "clyde-composites",
            postcode: "G1 1AA",
            submittedDaysAgo: 5,
            stateId: "submitted",
            payload: new BsonDocument
            {
                ["organisationName"] = "Clyde Composites Ltd",
                ["registrationNumber"] = "EPR-100134",
                ["operatorRegistrationId"] = "reg-006",
                ["material"] = "plastic",
                ["previousAccreditationYear"] = 2025,
                ["complianceIssuesReported"] = 0,
                ["operatorEmail"] = "clyde.composites@example.com",
                ["siteAddressPostcode"] = "G1 1AA"
            },
            submittedBy: "stub-portal-client",
            now: now);

        // Additional Wales item — assessment in progress.
        yield return Build(
            seedKey: "swansea-textiles",
            postcode: "SA1 1AA",
            submittedDaysAgo: 11,
            stateId: "assessment-in-progress",
            payload: new BsonDocument
            {
                ["organisationName"] = "Swansea Textiles Recovery",
                ["registrationNumber"] = "EPR-099441",
                ["operatorRegistrationId"] = "reg-007",
                ["material"] = "glass",
                ["previousAccreditationYear"] = 2024,
                ["complianceIssuesReported"] = 1,
                ["operatorEmail"] = "swansea.textiles@example.com",
                ["siteAddressPostcode"] = "SA1 1AA"
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
                    "confirm-application-completeness"
                ],
                ["duly-made"] =
                [
                    "confirm-registration-fee-paid"
                ],
                ["assessment-in-progress"] =
                [
                    "review-compliance-history"
                ]
            });

        // Additional Northern Ireland item — submitted, unassigned.
        yield return Build(
            seedKey: "belfast-fibres",
            postcode: "BT7 1AA",
            submittedDaysAgo: 2,
            stateId: "submitted",
            payload: new BsonDocument
            {
                ["organisationName"] = "Belfast Fibres Co",
                ["registrationNumber"] = "EPR-100198",
                ["operatorRegistrationId"] = "reg-008",
                ["material"] = "paper",
                ["previousAccreditationYear"] = 2025,
                ["complianceIssuesReported"] = 0,
                ["operatorEmail"] = "belfast.fibres@example.com",
                ["siteAddressPostcode"] = "BT7 1AA"
            },
            submittedBy: "stub-portal-client",
            now: now);

        // RA-254: carries every field a real operator submission can send —
        // including submittedBy, prns, businessPlan and samplingPlan, which
        // none of the items above populate. Used by the mgmt-tests e2e suite
        // to verify the Application details page renders the full payload
        // rather than just the subset the other seed items happen to cover.
        yield return Build(
            seedKey: "full-payload-verification",
            postcode: "EC1A 1BB",
            submittedDaysAgo: 4,
            stateId: "submitted",
            payload: new BsonDocument
            {
                ["organisationName"] = "Full Payload Verification Ltd",
                ["registrationNumber"] = "EPR-100999",
                ["operatorApplicationId"] = "app-full-payload-001",
                ["operatorOrganisationId"] = "org-full-payload-001",
                ["operatorRegistrationId"] = "reg-full-payload-001",
                ["material"] = "plastic",
                ["accreditationYear"] = 2026,
                ["previousAccreditationYear"] = 2025,
                ["complianceIssuesReported"] = 0,
                ["operatorEmail"] = "full.payload@example.com",
                ["siteAddress"] = "1 Full Payload Lane, London",
                ["siteAddressPostcode"] = "EC1A 1BB",
                ["submittedBy"] = new BsonDocument
                {
                    ["fullName"] = "Priya Sharma",
                    ["jobTitle"] = "Compliance Manager",
                    ["email"] = "priya.sharma@example.com"
                },
                ["prns"] = new BsonDocument
                {
                    ["plannedTonnageBand"] = "UpTo1000",
                    ["authorisers"] = new BsonArray
                    {
                        new BsonDocument
                        {
                            ["fullName"] = "Tom Baker",
                            ["email"] = "tom.baker@example.com"
                        }
                    }
                },
                ["businessPlan"] = new BsonDocument
                {
                    ["newInfrastructurePercent"] = 20,
                    ["priceSupportPercent"] = 15,
                    ["businessCollectionsPercent"] = 25,
                    ["communicationsPercent"] = 10,
                    ["newMarketsPercent"] = 20,
                    ["newUsesPercent"] = 10,
                    ["newInfrastructureDetail"] = "New sorting line investment",
                    ["priceSupportDetail"] = "Subsidised collection scheme",
                    ["businessCollectionsDetail"] = "Kerbside collection expansion",
                    ["communicationsDetail"] = "Customer awareness campaign",
                    ["newMarketsDetail"] = "New export contracts secured",
                    ["newUsesDetail"] = "Recycled content packaging trial"
                },
                ["samplingPlan"] = new BsonDocument
                {
                    ["files"] = new BsonArray
                    {
                        new BsonDocument
                        {
                            ["filename"] = "sampling-plan.pdf",
                            ["uploadedAt"] = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc),
                            ["scanStatus"] = "Clean"
                        }
                    }
                }
            },
            submittedBy: "stub-portal-client",
            now: now);
    }

    /// <summary>
    /// Build a single seeded work item with a realistic audit trail.
    ///
    /// RA-175: the method is no longer <c>static</c> so it can access
    /// the injected <see cref="INationResolver"/> to derive
    /// <c>payload.nation</c> from the postcode using the same logic that
    /// <see cref="ReAccreditationNationRoutingHook"/> applies to real
    /// submissions. The audit log mirrors what
    /// <see cref="WorkItemService"/> and the hooks write, so the seeded
    /// items have a plausible processing history.
    /// </summary>
    private WorkItem Build(
        string seedKey,
        string postcode,
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

        // RA-175: derive nation from postcode using the same resolver as
        // ReAccreditationNationRoutingHook so the camelCase payload.nation
        // key matches the MongoDB index and the nation filter in the work
        // queue correctly surfaces this item.
        var nation = nationResolver.Resolve(postcode);
        payload["nation"] = nation.ToString();

        // RA-196: ensure applicationReference is in the payload.
        var applicationReference = GenerateDeterministicReference(seedKey);
        payload["applicationReference"] = applicationReference;

        var workItem = new WorkItem
        {
            // Deterministic id keyed by (TypeId, seedKey) so re-running
            // the seeder is idempotent and concurrent instances cannot
            // create duplicates (epr-33c).
            Id = WorkItemSeed.DeterministicId(ReAccreditationType.Id, seedKey),
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            SubmittedAt = submittedAt,
            LastModifiedAt = lastModifiedAt,
            SubmittedBy = submittedBy,
            AssignedToId = assignedToId,
            AssignedToName = assignedToName,
            AssignedAt = assignedAt,
            // epr-ce4: seeded assignments are attributed to a sentinel
            // ("system:seeder"), never to the assignee id — that would
            // falsify the audit trail to claim the user assigned
            // themselves.
            AssignedBy = assignedToId is null ? null : SeederAssignedBy,
            Payload = payload
        };

        if (completedTasks is not null)
        {
            foreach (var (state, tasks) in completedTasks)
            {
                workItem.CompletedTaskIdsByState[state] = new HashSet<string>(tasks, StringComparer.OrdinalIgnoreCase);
            }
        }

        // RA-175: seed a realistic audit trail so the timeline view has
        // plausible history.  Mirrors what WorkItemService.SubmitAsync and
        // the post-submission hooks append for real items.

        // Birth event (mirrors WorkItemService.SubmitAsync).
        workItem.AuditLog.Add(new WorkItemAuditEntry
        {
            Action = "work-item-submitted",
            ActionDisplayName = "Work item submitted",
            Details = new Dictionary<string, string?>
            {
                ["typeId"] = ReAccreditationType.Id,
                ["stateId"] = stateId,
                ["source"] = "seeder",
                ["clientId"] = submittedBy,
                ["applicationReference"] = applicationReference
            },
            CreatedAt = submittedAt,
            CreatedBy = submittedBy,
            CreatedByName = null
        });

        // Nation routing event (mirrors ReAccreditationNationRoutingHook).
        workItem.AuditLog.Add(new WorkItemAuditEntry
        {
            Action = "routed-to-nation",
            ActionDisplayName = "Routed to nation",
            Details = new Dictionary<string, string?>
            {
                ["nation"] = nation.ToString(),
                ["derivedFrom"] = "site-address"
            },
            CreatedAt = submittedAt.AddSeconds(1),
            CreatedBy = null,
            CreatedByName = null
        });

        // Assignment event for assigned items (mirrors WorkItemService.AssignAsync).
        if (assignedToId is not null && assignedAt is not null)
        {
            workItem.AuditLog.Add(new WorkItemAuditEntry
            {
                Action = "assigned",
                ActionDisplayName = "Assigned",
                Details = new Dictionary<string, string?>
                {
                    ["assigneeId"] = assignedToId,
                    ["assigneeName"] = assignedToName,
                    ["previousAssigneeId"] = null,
                    ["previousAssigneeName"] = null
                },
                CreatedAt = assignedAt.Value,
                CreatedBy = SeederAssignedBy,
                CreatedByName = null
            });
        }

        return workItem;
    }

    private static string GenerateDeterministicReference(string seedKey)
    {
        var input = System.Text.Encoding.UTF8.GetBytes(seedKey);
        var hash = System.Security.Cryptography.SHA1.HashData(input);

        // Simple stable uint from first 4 bytes
        uint val = ((uint)hash[0] << 24) | ((uint)hash[1] << 16) | ((uint)hash[2] << 8) | (uint)hash[3];
        var digits = 100_000_000 + (val % 900_000_000);
        return $"RA-{digits}";
    }
}