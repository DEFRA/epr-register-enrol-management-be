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

    // ------------------------------- idempotency -------------------------------

    [Theory]
    [InlineData("submitted")]
    [InlineData("duly-made")]
    [InlineData("assessment-in-progress")]
    [InlineData("awaiting-decision")]
    public async Task ResumeFromQueryAsync_is_an_idempotent_replay_when_already_resumed(string stateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(queryActionId: null, stateId: stateId);

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
    public async Task ResumeFromQueryAsync_fails_with_invalid_transition_from_a_decided_outcome(string stateId)
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
    public async Task ResumeFromQueryAsync_returns_not_found_when_the_caller_cannot_read_the_work_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("query-during-duly-making", submittedBy: "another-tenant");

        var result = await harness.Service.ResumeFromQueryAsync(
            harness.WorkItem.Id, s_request, harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
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
