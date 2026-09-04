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

    [Fact]
    public async Task Constructor_defaults_time_provider_when_omitted()
    {
        // Covers the `timeProvider ?? TimeProvider.System` branch, which the
        // Harness below never exercises because it always supplies a
        // FakeTimeProvider explicitly.
        var persistence = Substitute.For<IWorkItemPersistence>();
        var engine = Substitute.For<IWorkItemService>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        var workItem = new WorkItem
        {
            Id = Guid.NewGuid(),
            TypeId = ReAccreditationType.Id,
            StateId = "queried",
            SubmittedBy = TenantClientId,
            Payload = new BsonDocument(),
            AuditLog =
            [
                new WorkItemAuditEntry
                {
                    Action = ReAccreditationQueryService.AuditAction,
                    ActionDisplayName = "Queried",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = "alice-1",
                    Details = new Dictionary<string, string?>
                    {
                        ["actionId"] = "query-during-assessment",
                    },
                },
            ],
        };
        persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);
        persistence
            .SetPayloadFieldAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<BsonValue>(), Arg.Any<CancellationToken>())
            .Returns(true);
        engine
            .ApplyActionAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(WorkItemActionResult.Success(workItem));
        // RA-523: the query audit entry above names a raising user and the work
        // item is unassigned, so the resume restores the assignment through the
        // engine on its way out.
        engine
            .AssignAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(WorkItemActionResult.Success(workItem));
        auditAppender
            .AppendAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(), Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("user:id", "alice-1"), new Claim("client_id", TenantClientId)], "test"));
        var service = new ReAccreditationResumeService(
            persistence, engine, auditAppender, NullLogger<ReAccreditationResumeService>.Instance);

        var result = await service.ResumeFromQueryAsync(
            workItem.Id, s_request, user, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

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
    public async Task ResumeFromQueryAsync_merges_a_canonical_section_onto_its_top_level_payload_field()
    {
        // RA-291 regression: a resubmitted section whose key matches
        // s_canonicalPayloadFieldBySectionKey (e.g. "BusinessPlan") must also
        // be merged onto payload.businessPlan, not just latestSections.
        // s_request's own "business-plan" key is deliberately kebab-case (a
        // ReAccreditationQuerySections key, not this canonical map's key), so
        // this needs its own request.
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-assessment");
        var request = new ResumeFromQueryRequest(
            new ResponderContactDetails("Jane Doe", "jane@example.com", "Manager"),
            ["business-plan"],
            new Dictionary<string, JsonElement>
            {
                ["BusinessPlan"] = JsonDocument.Parse("""{"newInfrastructurePercent":20}""").RootElement,
            },
            []);

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, request, harness.User, ct);

        Assert.True(result.IsSuccess);
        await harness.Persistence.Received(1).SetPayloadFieldAsync(
            harness.WorkItem.Id, "businessPlan", Arg.Any<BsonValue>(), ct);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_reports_not_found_when_the_canonical_field_write_finds_no_item()
    {
        // Covers the canonical-field SetPayloadFieldAsync's own `!canonicalMatched`
        // guard, distinct from the final latestSections write's not-found guard
        // covered by ResumeFromQueryAsync_reports_not_found_when_the_item_vanishes_before_the_stamp.
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-assessment");
        harness.Persistence
            .SetPayloadFieldAsync(
                harness.WorkItem.Id, "businessPlan", Arg.Any<BsonValue>(), ct)
            .Returns(false);
        var request = new ResumeFromQueryRequest(
            new ResponderContactDetails("Jane Doe", "jane@example.com", "Manager"),
            ["business-plan"],
            new Dictionary<string, JsonElement>
            {
                ["BusinessPlan"] = JsonDocument.Parse("""{"newInfrastructurePercent":20}""").RootElement,
            },
            []);

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, request, harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
        await harness.Engine.DidNotReceiveWithAnyArgs()
            .ApplyActionAsync(default, default!, default!, ct);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_falls_back_to_the_engine_result_when_the_post_update_reread_finds_nothing()
    {
        // Covers the final `refreshed is null` arm: the item existed for the
        // initial load and the transition succeeded, but vanished (e.g.
        // concurrently archived) before the closing re-read that normally
        // picks up the query-responded audit entry.
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-assessment");
        harness.Persistence
            .GetByIdAsync(harness.WorkItem.Id, ct)
            .Returns(harness.WorkItem, (WorkItem?)null);

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, s_request, harness.User, ct);

        Assert.True(result.IsSuccess);
        Assert.Same(harness.WorkItem, result.WorkItem);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_accepts_a_minimal_request_with_no_sections_or_responder_details()
    {
        // Every field on ResumeFromQueryRequest is nullable (validated
        // separately by ReAccreditationResumeValidator at the endpoint);
        // this covers the service's own null-coalescing fallbacks for
        // SectionKeys, Sections, ResponderContactDetails and FileReferences.
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-assessment");
        var request = new ResumeFromQueryRequest(null, null, null, null);

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, request, harness.User, ct);

        Assert.True(result.IsSuccess);
        await harness.AuditAppender.Received(1).AppendAsync(
            harness.WorkItem.Id,
            ReAccreditationResumeService.AuditAction,
            ReAccreditationResumeService.AuditActionDisplayName,
            Arg.Is<Dictionary<string, string?>>(d =>
                d["sectionKeys"] == ""
                && d["responderFullName"] == null
                && d["responderEmail"] == null
                && d["responderRole"] == null),
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

    // RA-523: 'updated' is the resume target for three origins;
    // 'assessment-in-progress' is the resume target for the duly-made origin.
    // A resume retry landing on either is a replay, not a conflict.
    [Theory]
    [InlineData("updated")]
    [InlineData("assessment-in-progress")]
    public async Task ResumeFromQueryAsync_is_an_idempotent_replay_when_already_resumed(
        string stateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(queryActionId: null, stateId: stateId);

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, s_request, harness.User, ct);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsIdempotentReplay);
        await harness.Engine.DidNotReceiveWithAnyArgs()
            .ApplyActionAsync(default, default!, default!, ct);
        await harness.Persistence.DidNotReceiveWithAnyArgs()
            .SetPayloadFieldAsync(default, default!, default!, ct);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    // RA-337: once resumed, a work item passes through 'submitted' /
    // 'duly-made' / 'awaiting-decision' via continue-review-during-*, not
    // resume-during-* directly, so a resume retry landing on one of those
    // states is a real conflict now, not an idempotent replay. RA-523:
    // 'assessment-in-progress' is NOT here — it is a valid resume target for
    // the duly-made origin, so a retry there is a replay (asserted above).
    [InlineData("submitted")]
    [InlineData("duly-made")]
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
            .ApplyActionAsync(default, default!, default!, ct);
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
            .AppendAsync(default, default!, default!, default!, default!, ct);
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
            .ApplyActionAsync(default, default!, default!, ct);
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

    // ------------------------- RA-523: assignment restore -------------------------

    /// <summary>
    /// RA-523 AC02: an application that comes back from the operator owned by
    /// nobody is handed to the case worker who raised the query, read off that
    /// query's own audit entry, and routed through the engine so the normal
    /// `assigned` audit entry and RA-237 notification fire (AC05).
    /// </summary>
    [Fact]
    public async Task Resume_assigns_an_unassigned_item_to_the_querying_case_worker()
    {
        var harness = new Harness(
            "query-during-assessment",
            querierId: "carol-3",
            querierName: "Carol Example");

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, s_request, harness.User, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        await harness.Engine.Received(1).AssignAsync(
            harness.WorkItem.Id,
            "carol-3",
            "Carol Example",
            harness.User,
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// RA-523: an application a colleague deliberately took over mid-query stays
    /// theirs — an operator-driven resubmission must not undo a human decision.
    /// </summary>
    [Fact]
    public async Task Resume_leaves_an_already_assigned_item_alone()
    {
        var harness = new Harness(
            "query-during-assessment",
            querierId: "carol-3",
            querierName: "Carol Example",
            assignedToId: "bob-2",
            assignedToName: "Bob Example");

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, s_request, harness.User, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        await harness.Engine.DidNotReceive().AssignAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// RA-523: a query audit entry that records no raising user (a pre-RA-97 or
    /// machine-raised entry) leaves nothing to restore to. The resume itself
    /// still succeeds — the state change is what the operator backend depends on.
    /// </summary>
    [Fact]
    public async Task Resume_succeeds_when_the_query_entry_names_no_raising_user()
    {
        var harness = new Harness("query-during-assessment");

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, s_request, harness.User, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        await harness.Engine.DidNotReceive().AssignAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// RA-523: a failed assignment restore must never fail the operator's
    /// resubmission — the transition is already persisted, so reporting an error
    /// would make a completed resume look rejected.
    /// </summary>
    [Fact]
    public async Task Resume_still_succeeds_when_the_assignment_restore_fails()
    {
        var harness = new Harness(
            "query-during-assessment",
            querierId: "carol-3",
            querierName: "Carol Example");
        harness.Engine
            .AssignAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidAssignment, "nope"));

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, s_request, harness.User, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        // A non-concurrency failure is not retried — there is nothing to settle.
        await harness.Engine.Received(1).AssignAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// RA-523: the restore must not fail the resume even when the engine
    /// THROWS rather than returning a failure code — an infrastructure fault
    /// (Mongo blip, driver timeout) would otherwise surface an already-persisted
    /// resubmission to the operator backend as a 5xx, the exact failure mode
    /// running the restore after the transition exists to prevent.
    /// </summary>
    [Fact]
    public async Task Resume_still_succeeds_when_the_assignment_restore_throws()
    {
        var harness = new Harness(
            "query-during-assessment",
            querierId: "carol-3",
            querierName: "Carol Example");
        harness.Engine
            .AssignAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns<Task<WorkItemActionResult>>(_ => throw new TimeoutException("mongo is having a moment"));

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, s_request, harness.User, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    /// <summary>
    /// RA-523: a cancellation raised by the caller's own token is NOT swallowed
    /// — that is the caller abandoning the request, not the restore failing.
    /// </summary>
    [Fact]
    public async Task Resume_propagates_a_caller_cancellation_from_the_assignment_restore()
    {
        var harness = new Harness(
            "query-during-assessment",
            querierId: "carol-3",
            querierName: "Carol Example");
        using var cts = new CancellationTokenSource();
        harness.Engine
            .AssignAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns<Task<WorkItemActionResult>>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Service.ResumeFromQueryAsync(
                harness.WorkItem.Id, s_request, harness.User, cts.Token));
    }

    /// <summary>
    /// RA-523: the resume transition's post-action hooks write to the same
    /// document, some on the background queue, so the restore routinely loses
    /// the optimistic-concurrency race. It retries rather than dropping the
    /// assignment.
    /// </summary>
    [Fact]
    public async Task Resume_retries_the_assignment_restore_after_a_concurrency_conflict()
    {
        var harness = new Harness(
            "query-during-assessment",
            querierId: "carol-3",
            querierName: "Carol Example");
        harness.Engine
            .AssignAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.ConcurrencyConflict, "conflict"),
                _ => WorkItemActionResult.Success(harness.WorkItem));

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, s_request, harness.User, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        await harness.Engine.Received(2).AssignAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    private sealed class Harness
    {
        public Harness(
            string? queryActionId,
            string stateId = "queried",
            bool seedWorkItem = true,
            string typeId = ReAccreditationType.Id,
            string submittedBy = TenantClientId,
            string? querierId = null,
            string? querierName = null,
            string? assignedToId = null,
            string? assignedToName = null)
        {
            WorkItem = new WorkItem
            {
                TypeId = typeId,
                StateId = stateId,
                SubmittedBy = submittedBy,
                AssignedToId = assignedToId,
                AssignedToName = assignedToName,
            };

            if (queryActionId is not null)
            {
                WorkItem.AuditLog.Add(new WorkItemAuditEntry
                {
                    Action = ReAccreditationQueryService.AuditAction,
                    ActionDisplayName = "Application queried",
                    CreatedAt = s_now.UtcDateTime.AddHours(-1),
                    CreatedBy = querierId,
                    CreatedByName = querierName,
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
            Engine
                .AssignAsync(
                    Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
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
