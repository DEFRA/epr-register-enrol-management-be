using System.Security.Claims;
using System.Text.Json;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-311/MBE-1: the resume service resolves the right <c>resume-during-*</c>
/// action from the work item's own query audit history (the inverse of
/// <see cref="ReAccreditationQueryService"/>'s state-driven lookup),
/// delegates the state change to the framework engine, and records the
/// resubmitted sections + responder details on the audit log.
/// </summary>
public class ReAccreditationResumeServiceTests
{
    private const string TenantClientId = "test-client";

    private static readonly ResumeFromQueryRequest s_request = new(
        new ResponderContactDetails("Jane Doe", "jane@example.com", "Manager"),
        ["business-plan", "prn-tonnage"],
        new Dictionary<string, JsonElement>
        {
            ["business-plan"] = JsonDocument.Parse("""{"newInfrastructurePercent":20}""").RootElement,
        },
        [new SectionFileReference("prn-tonnage", "file-1", "evidence.pdf", "s3/key/evidence.pdf")]);

    private static readonly DateTimeOffset s_now = new(2026, 7, 20, 9, 30, 0, TimeSpan.Zero);

    // --------------------------- happy path per state ---------------------------

    [Theory]
    [InlineData("query-during-duly-making", "resume-during-duly-making")]
    [InlineData("query-during-duly-made", "resume-during-duly-made")]
    [InlineData("query-during-assessment", "resume-during-assessment")]
    [InlineData("query-during-decision", "resume-during-decision")]
    public async Task ResumeFromQueryAsync_applies_the_inverse_action_for_the_original_query(
        string queryActionId,
        string expectedResumeActionId)
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(queryActionId);

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, s_request, harness.User, ct);

        Assert.True(result.IsSuccess);
        await harness.Engine.Received(1).ApplyActionAsync(
            harness.WorkItem.Id, expectedResumeActionId, harness.User, ct);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_records_the_resume_detail_on_the_audit_log()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-assessment");

        await harness.Service.ResumeFromQueryAsync(harness.WorkItem.Id, s_request, harness.User, ct);

        await harness.AuditAppender.Received(1).AppendAsync(
            harness.WorkItem.Id,
            ReAccreditationResumeService.AuditAction,
            ReAccreditationResumeService.AuditActionDisplayName,
            Arg.Is<Dictionary<string, string?>>(d =>
                d["actionId"] == "resume-during-assessment"
                && d["sectionKeys"] == "business-plan,prn-tonnage"
                && d["responderFullName"] == "Jane Doe"
                && d["responderEmail"] == "jane@example.com"
                && d["responderRole"] == "Manager"
                && d["fileReferences"] == "prn-tonnage:file-1:evidence.pdf"),
            harness.User,
            ct);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_stamps_latest_sections_before_transitioning()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-duly-making");

        await harness.Service.ResumeFromQueryAsync(harness.WorkItem.Id, s_request, harness.User, ct);

        Received.InOrder(() =>
        {
            harness.Persistence.SetPayloadFieldAsync(
                harness.WorkItem.Id,
                ReAccreditationResumeService.LatestSectionsPayloadField,
                Arg.Any<BsonValue>(),
                ct);
            harness.Engine.ApplyActionAsync(
                harness.WorkItem.Id, "resume-during-duly-making", harness.User, ct);
        });
    }

    [Fact]
    public async Task ResumeFromQueryAsync_stamps_section_values_and_file_references()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-duly-making");
        BsonValue? stamped = null;
        harness.Persistence
            .SetPayloadFieldAsync(
                Arg.Any<Guid>(),
                ReAccreditationResumeService.LatestSectionsPayloadField,
                Arg.Do<BsonValue>(v => stamped = v),
                Arg.Any<CancellationToken>())
            .Returns(true);

        await harness.Service.ResumeFromQueryAsync(harness.WorkItem.Id, s_request, harness.User, ct);

        var doc = stamped!.AsBsonDocument;
        Assert.Equal(
            ["business-plan", "prn-tonnage"],
            doc["sectionKeys"].AsBsonArray.Select(v => v.AsString));
        Assert.Equal(20, doc["sections"]["business-plan"]["newInfrastructurePercent"].AsInt32);
        var fileRef = Assert.Single(doc["fileReferences"].AsBsonArray);
        Assert.Equal("prn-tonnage", fileRef["sectionKey"].AsString);
        Assert.Equal("file-1", fileRef["fileId"].AsString);
        Assert.Equal(s_now.UtcDateTime, doc["respondedAt"].ToUniversalTime());
        Assert.Equal("alice-1", doc["respondedBy"].AsString);
    }

    // --------------------- RA-413 canonical payload merge ----------------------

    private static ResumeFromQueryRequest ResumeRequest(
        IReadOnlyList<string> sectionKeys,
        Dictionary<string, JsonElement>? sections = null,
        IReadOnlyList<SectionFileReference>? fileReferences = null) =>
        new(
            new ResponderContactDetails("Jane Doe", "jane@example.com", "Manager"),
            sectionKeys,
            sections,
            fileReferences);

    private static Dictionary<string, BsonValue> CaptureCanonicalWrites(Harness harness)
    {
        var writes = new Dictionary<string, BsonValue>(StringComparer.Ordinal);
        harness.Persistence
            .SetPayloadFieldAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<BsonValue>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                writes[(string)ci[1]!] = (BsonValue)ci[2]!;
                return true;
            });
        return writes;
    }

    [Fact]
    public async Task ResumeFromQueryAsync_merges_prn_tonnage_and_authority_into_canonical_prns()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-assessment");
        var writes = CaptureCanonicalWrites(harness);

        var request = ResumeRequest(
            ["prn-tonnage", "authority-to-issue"],
            new Dictionary<string, JsonElement>
            {
                ["prn-tonnage"] = JsonDocument.Parse("""{"plannedTonnageBand":"OneThousandToFiveThousand"}""").RootElement,
                ["authority-to-issue"] = JsonDocument.Parse("""{"authorisers":[{"fullName":"New Auth","email":"na@example.com"}]}""").RootElement,
            });

        await harness.Service.ResumeFromQueryAsync(harness.WorkItem.Id, request, harness.User, ct);

        var prns = Assert.Contains(ReAccreditationResumeService.PrnsPayloadField, writes).AsBsonDocument;
        Assert.Equal("OneThousandToFiveThousand", prns["plannedTonnageBand"].AsString);
        var authoriser = Assert.Single(prns["authorisers"].AsBsonArray).AsBsonDocument;
        Assert.Equal("New Auth", authoriser["fullName"].AsString);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_merges_only_resubmitted_prns_fields_and_preserves_siblings()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-assessment");
        // Pre-query prns carries both a band and an authoriser; only the band
        // is resubmitted, so the authoriser must survive untouched.
        harness.WorkItem.Payload["prns"] = new BsonDocument
        {
            ["plannedTonnageBand"] = "UpTo1000",
            ["authorisers"] = new BsonArray
            {
                new BsonDocument { ["fullName"] = "Original Auth", ["email"] = "orig@example.com" },
            },
        };
        var writes = CaptureCanonicalWrites(harness);

        var request = ResumeRequest(
            ["prn-tonnage"],
            new Dictionary<string, JsonElement>
            {
                ["prn-tonnage"] = JsonDocument.Parse("""{"plannedTonnageBand":"OneThousandToFiveThousand"}""").RootElement,
            });

        await harness.Service.ResumeFromQueryAsync(harness.WorkItem.Id, request, harness.User, ct);

        var prns = Assert.Contains(ReAccreditationResumeService.PrnsPayloadField, writes).AsBsonDocument;
        Assert.Equal("OneThousandToFiveThousand", prns["plannedTonnageBand"].AsString);
        var authoriser = Assert.Single(prns["authorisers"].AsBsonArray).AsBsonDocument;
        Assert.Equal("Original Auth", authoriser["fullName"].AsString);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_writes_the_business_plan_section_to_canonical_business_plan()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-assessment");
        var writes = CaptureCanonicalWrites(harness);

        var request = ResumeRequest(
            ["business-plan"],
            new Dictionary<string, JsonElement>
            {
                ["business-plan"] = JsonDocument.Parse("""{"newInfrastructurePercent":42,"newInfrastructureDetail":"Updated plan"}""").RootElement,
            });

        await harness.Service.ResumeFromQueryAsync(harness.WorkItem.Id, request, harness.User, ct);

        var businessPlan = Assert.Contains(ReAccreditationResumeService.BusinessPlanPayloadField, writes).AsBsonDocument;
        Assert.Equal(42, businessPlan["newInfrastructurePercent"].AsInt32);
        Assert.Equal("Updated plan", businessPlan["newInfrastructureDetail"].AsString);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_rebuilds_sampling_plan_files_from_file_references()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-assessment");
        var writes = CaptureCanonicalWrites(harness);

        var request = ResumeRequest(
            ["sampling-and-inspection-plan"],
            fileReferences:
            [
                new SectionFileReference(
                    "sampling-and-inspection-plan", "si-1", "sampling-plan-v2.pdf", "sampling-plans/x/si-1.pdf"),
                // A file reference for a different section must not leak into
                // payload.samplingPlan.files.
                new SectionFileReference("prn-tonnage", "prn-1", "prn.pdf", "prns/x/prn-1.pdf"),
            ]);

        await harness.Service.ResumeFromQueryAsync(harness.WorkItem.Id, request, harness.User, ct);

        var samplingPlan = Assert.Contains(ReAccreditationResumeService.SamplingPlanPayloadField, writes).AsBsonDocument;
        var file = Assert.Single(samplingPlan["files"].AsBsonArray).AsBsonDocument;
        Assert.Equal("si-1", file["fileId"].AsString);
        Assert.Equal("sampling-plan-v2.pdf", file["filename"].AsString);
        Assert.Equal("sampling-plans/x/si-1.pdf", file["s3Key"].AsString);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_replaces_sampling_files_but_preserves_other_sampling_fields()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-assessment");
        harness.WorkItem.Payload["samplingPlan"] = new BsonDocument
        {
            ["someOtherField"] = "keep-me",
            ["files"] = new BsonArray
            {
                new BsonDocument { ["fileId"] = "old-file", ["filename"] = "old.pdf", ["s3Key"] = "old/key" },
            },
        };
        var writes = CaptureCanonicalWrites(harness);

        var request = ResumeRequest(
            ["sampling-and-inspection-plan"],
            fileReferences:
            [
                new SectionFileReference(
                    "sampling-and-inspection-plan", "new-file", "new.pdf", "new/key"),
            ]);

        await harness.Service.ResumeFromQueryAsync(harness.WorkItem.Id, request, harness.User, ct);

        var samplingPlan = Assert.Contains(ReAccreditationResumeService.SamplingPlanPayloadField, writes).AsBsonDocument;
        Assert.Equal("keep-me", samplingPlan["someOtherField"].AsString);
        var file = Assert.Single(samplingPlan["files"].AsBsonArray).AsBsonDocument;
        Assert.Equal("new-file", file["fileId"].AsString);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_does_not_touch_canonical_fields_for_unsubmitted_sections()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-assessment");
        var writes = CaptureCanonicalWrites(harness);

        // Only prn-tonnage resubmitted: businessPlan and samplingPlan must not
        // be written at all.
        var request = ResumeRequest(
            ["prn-tonnage"],
            new Dictionary<string, JsonElement>
            {
                ["prn-tonnage"] = JsonDocument.Parse("""{"plannedTonnageBand":"UpTo1000"}""").RootElement,
            });

        await harness.Service.ResumeFromQueryAsync(harness.WorkItem.Id, request, harness.User, ct);

        Assert.True(writes.ContainsKey(ReAccreditationResumeService.PrnsPayloadField));
        Assert.False(writes.ContainsKey(ReAccreditationResumeService.BusinessPlanPayloadField));
        Assert.False(writes.ContainsKey(ReAccreditationResumeService.SamplingPlanPayloadField));
    }

    [Fact]
    public async Task ResumeFromQueryAsync_canonical_merge_writes_before_transitioning()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-duly-making");

        var request = ResumeRequest(
            ["business-plan"],
            new Dictionary<string, JsonElement>
            {
                ["business-plan"] = JsonDocument.Parse("""{"newInfrastructurePercent":1}""").RootElement,
            });

        await harness.Service.ResumeFromQueryAsync(harness.WorkItem.Id, request, harness.User, ct);

        Received.InOrder(() =>
        {
            harness.Persistence.SetPayloadFieldAsync(
                harness.WorkItem.Id,
                ReAccreditationResumeService.BusinessPlanPayloadField,
                Arg.Any<BsonValue>(),
                ct);
            harness.Engine.ApplyActionAsync(
                harness.WorkItem.Id, "resume-during-duly-making", harness.User, ct);
        });
    }

    // ------------------------------- idempotency -------------------------------

    [Fact]
    public async Task ResumeFromQueryAsync_is_an_idempotent_replay_when_already_resumed()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(queryActionId: null, stateId: "updated");

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, s_request, harness.User, ct);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsIdempotentReplay);
        await harness.Engine.DidNotReceiveWithAnyArgs()
            .ApplyActionAsync(default, default!, default!, default);
        await harness.Persistence.DidNotReceiveWithAnyArgs()
            .SetPayloadFieldAsync(default, default!, default!, default);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    // RA-337: once resumed, a work item passes through 'submitted' /
    // 'duly-made' / 'assessment-in-progress' / 'awaiting-decision' via
    // continue-review-during-*, not resume-during-* directly, so a resume
    // retry landing on one of those states is a real conflict now, not an
    // idempotent replay.
    [InlineData("submitted")]
    [InlineData("duly-made")]
    [InlineData("assessment-in-progress")]
    [InlineData("awaiting-decision")]
    public async Task ResumeFromQueryAsync_fails_with_invalid_transition_when_not_queried_or_updated(string stateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(queryActionId: null, stateId: stateId);

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, s_request, harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        await harness.Engine.DidNotReceiveWithAnyArgs()
            .ApplyActionAsync(default, default!, default!, default);
    }

    // --------------------------- audit history resolution ---------------------------

    [Fact]
    public async Task ResumeFromQueryAsync_fails_when_no_application_queried_entry_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        // 'queried' with no 'application-queried' audit entry at all — should
        // not happen via the real query flow, but must not 500.
        var harness = new Harness(queryActionId: null, stateId: "queried");

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, s_request, harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_uses_the_most_recent_application_queried_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-duly-making");
        // An earlier (stale) query entry from a previous raise/resume cycle,
        // recorded before the current one, with a different action id.
        harness.WorkItem.AuditLog.Insert(0, new WorkItemAuditEntry
        {
            Action = ReAccreditationQueryService.AuditAction,
            ActionDisplayName = "Application queried",
            CreatedAt = s_now.UtcDateTime.AddDays(-10),
            Details = new Dictionary<string, string?> { ["actionId"] = "query-during-decision" },
        });

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, s_request, harness.User, ct);

        Assert.True(result.IsSuccess);
        await harness.Engine.Received(1).ApplyActionAsync(
            harness.WorkItem.Id, "resume-during-duly-making", harness.User, ct);
    }

    // --------------------------------- gating ---------------------------------

    [Fact]
    public async Task ResumeFromQueryAsync_returns_not_found_when_the_work_item_does_not_exist()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-duly-making", seedWorkItem: false);

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, s_request, harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_succeeds_for_a_work_item_not_submitted_by_the_caller()
    {
        // RBAC lives in the frontend now (ADR-0005) — the service performs
        // the resume regardless of who submitted the item.
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-duly-making", submittedBy: "another-tenant");

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, s_request, harness.User, ct);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_rejects_a_work_item_of_a_different_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-duly-making", typeId: "some-other-type");

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, s_request, harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.UnknownAction, result.FailureCode);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_propagates_an_engine_failure_without_writing_audit_detail()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-duly-making");
        harness.Engine
            .ApplyActionAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>())
            .Returns(WorkItemActionResult.Failure(
                WorkItemActionFailureCode.MissingActorIdentity, "no user"));

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, s_request, harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.MissingActorIdentity, result.FailureCode);
        await harness.AuditAppender.DidNotReceiveWithAnyArgs()
            .AppendAsync(default, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_still_succeeds_when_the_audit_detail_could_not_be_appended()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-duly-making");
        harness.AuditAppender
            .AppendAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(), Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, s_request, harness.User, ct);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_reports_not_found_when_the_item_vanishes_before_the_stamp()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-duly-making");
        harness.Persistence
            .SetPayloadFieldAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<BsonValue>(),
                Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, s_request, harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
        await harness.Engine.DidNotReceiveWithAnyArgs()
            .ApplyActionAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_rejects_null_arguments()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-duly-making");

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            harness.Service.ResumeFromQueryAsync(harness.WorkItem.Id, null!, harness.User, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            harness.Service.ResumeFromQueryAsync(harness.WorkItem.Id, s_request, null!, ct));
    }

    private sealed class Harness
    {
        public Harness(
            string? queryActionId,
            string stateId = "queried",
            bool seedWorkItem = true,
            string typeId = ReAccreditationType.Id,
            string submittedBy = TenantClientId)
        {
            WorkItem = new WorkItem
            {
                TypeId = typeId,
                StateId = stateId,
                SubmittedBy = submittedBy,
            };

            if (queryActionId is not null)
            {
                WorkItem.AuditLog.Add(new WorkItemAuditEntry
                {
                    Action = ReAccreditationQueryService.AuditAction,
                    ActionDisplayName = "Application queried",
                    CreatedAt = s_now.UtcDateTime.AddHours(-1),
                    Details = new Dictionary<string, string?> { ["actionId"] = queryActionId },
                });
            }

            Persistence = Substitute.For<IWorkItemPersistence>();
            Persistence
                .GetByIdAsync(WorkItem.Id, Arg.Any<CancellationToken>())
                .Returns(seedWorkItem ? WorkItem : null);
            Persistence
                .SetPayloadFieldAsync(
                    Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<BsonValue>(),
                    Arg.Any<CancellationToken>())
                .Returns(true);

            Engine = Substitute.For<IWorkItemService>();
            Engine
                .ApplyActionAsync(
                    Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(),
                    Arg.Any<CancellationToken>())
                .Returns(WorkItemActionResult.Success(WorkItem));

            AuditAppender = Substitute.For<IWorkItemAuditAppender>();
            AuditAppender
                .AppendAsync(
                    Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<Dictionary<string, string?>>(), Arg.Any<ClaimsPrincipal>(),
                    Arg.Any<CancellationToken>())
                .Returns(true);

            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("user:id", "alice-1"),
                    new Claim("user:name", "Alice Example"),
                    new Claim("cognito:client_id", TenantClientId),
                ],
                "test"));

            Service = new ReAccreditationResumeService(
                Persistence,
                Engine,
                AuditAppender,
                NullLogger<ReAccreditationResumeService>.Instance,
                new FakeTimeProvider(s_now));
        }

        public WorkItem WorkItem { get; }
        public IWorkItemPersistence Persistence { get; }
        public IWorkItemService Engine { get; }
        public IWorkItemAuditAppender AuditAppender { get; }
        public ClaimsPrincipal User { get; }
        public ReAccreditationResumeService Service { get; }
    }
}
