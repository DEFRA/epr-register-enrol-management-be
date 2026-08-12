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
    public async Task ResumeFromQueryAsync_preserves_ra292_ors_interim_and_authoriser_fields()
    {
        // RA-292: the operator backend now emits the SAME ORS and prns shapes on
        // the resume-from-query path as on submit, byte for byte. That is new
        // surface — the previous projection sent a weaker ORS section with no
        // orsId, no isNewSite and no interimSite, so a queried-then-resubmitted
        // work item had its interim data wiped.
        //
        // Sections are stamped through the same schemaless
        // WorkItemPayloadConverter.ToBson as the submit payload, so they survive
        // by construction — this pins that, because the failure mode (a typed
        // section model) would look like a fix rather than a regression.
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-duly-making");
        BsonValue? stamped = null;
        harness.Persistence
            .SetPayloadFieldAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Do<BsonValue>(v => stamped = v),
                Arg.Any<CancellationToken>())
            .Returns(true);

        var request = new ResumeFromQueryRequest(
            new ResponderContactDetails("Jane Doe", "jane@example.com", "Manager"),
            ["overseas-sites", "prn-tonnage"],
            new Dictionary<string, JsonElement>
            {
                ["overseas-sites"] = JsonDocument.Parse(
                    """
                    {
                      "sites": [
                        {
                          "siteId": 1,
                          "orsId": "ORS-2026-0292",
                          "isNewSite": true,
                          "repatriatedLoads": "3",
                          "conditionsOfExport": true,
                          "interimSite": { "siteNumber": "INT-001", "isNewSite": true }
                        }
                      ]
                    }
                    """).RootElement,
                ["prn-tonnage"] = JsonDocument.Parse(
                    """{"authorisers":[{"fullName":"Grace Adeyemi","isNew":true}]}""").RootElement,
            },
            []);

        await harness.Service.ResumeFromQueryAsync(harness.WorkItem.Id, request, harness.User, ct);

        var site = stamped!.AsBsonDocument["sections"]["overseas-sites"]["sites"][0].AsBsonDocument;
        Assert.Equal("ORS-2026-0292", site["orsId"].AsString);
        Assert.True(site["isNewSite"].AsBoolean);
        Assert.Equal("3", site["repatriatedLoads"].AsString);
        Assert.True(site["conditionsOfExport"].AsBoolean);
        Assert.Equal("INT-001", site["interimSite"]["siteNumber"].AsString);
        Assert.True(site["interimSite"]["isNewSite"].AsBoolean);

        var authoriser = stamped.AsBsonDocument["sections"]["prn-tonnage"]["authorisers"][0];
        Assert.Equal("Grace Adeyemi", authoriser["fullName"].AsString);
        Assert.True(authoriser["isNew"].AsBoolean);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_stamps_section_values_and_file_references()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-duly-making");
        BsonValue? stamped = null;
        harness.Persistence
            .SetPayloadFieldAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Do<BsonValue>(v => stamped = v),
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

    [Fact]
    public async Task ResumeFromQueryAsync_merges_resubmitted_sections_onto_their_canonical_payload_fields()
    {
        // RA-XXX regression test: the operator backend keys `sections` by its
        // own OperatorSection enum name (HttpCaseWorkingApiAdapter.BuildSectionsPayload),
        // e.g. "BusinessPlan"/"Prns"/"SamplingPlan" — NOT the kebab-case
        // ReAccreditationQuerySections keys used for sectionKeys. A prior fix
        // mis-keyed the canonical merge map with the kebab-case keys, so the
        // merge always missed and the case management summary page kept
        // showing stale business plan / PRN / sampling plan values after a
        // resubmission.
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-duly-making");

        var request = new ResumeFromQueryRequest(
            new ResponderContactDetails("Jane Doe", "jane@example.com", "Manager"),
            ["business-plan", "prn-tonnage", "sampling-and-inspection-plan"],
            new Dictionary<string, JsonElement>
            {
                ["BusinessPlan"] = JsonDocument.Parse("""{"newInfrastructurePercent":20}""").RootElement,
                ["Prns"] = JsonDocument.Parse("""{"tonnage":123}""").RootElement,
                ["SamplingPlan"] = JsonDocument.Parse("""{"files":[]}""").RootElement,
            },
            []);

        await harness.Service.ResumeFromQueryAsync(harness.WorkItem.Id, request, harness.User, ct);

        await harness.Persistence.Received(1).SetPayloadFieldAsync(
            harness.WorkItem.Id,
            "businessPlan",
            Arg.Is<BsonValue>(v => v["newInfrastructurePercent"].AsInt32 == 20),
            ct);
        await harness.Persistence.Received(1).SetPayloadFieldAsync(
            harness.WorkItem.Id,
            "prns",
            Arg.Is<BsonValue>(v => v["tonnage"].AsInt32 == 123),
            ct);
        await harness.Persistence.Received(1).SetPayloadFieldAsync(
            harness.WorkItem.Id,
            "samplingPlan",
            Arg.Any<BsonValue>(),
            ct);
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
                    new Claim("client_id", TenantClientId),
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
