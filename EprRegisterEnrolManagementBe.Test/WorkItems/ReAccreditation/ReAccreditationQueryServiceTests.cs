using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-291: the query service resolves the right <c>query-during-*</c> action
/// from the work item's current state, delegates the state change to the
/// framework engine, and records the queried sections + reason on the audit
/// log. RA-000: the self-assign and the <c>currentQuery</c> stamp go to the
/// engine with the transition, as one <see cref="WorkItemActionWrite"/>.
/// </summary>
public class ReAccreditationQueryServiceTests
{
    private const string TenantClientId = "test-client";

    private static readonly string[] s_sections = ["business-plan", "prn-tonnage"];

    private static readonly DateTimeOffset s_now = new(2026, 7, 20, 9, 30, 0, TimeSpan.Zero);

    // -------------------------- action resolution --------------------------

    [Theory]
    [InlineData("submitted", "query-during-duly-making")]
    [InlineData("duly-made", "query-during-duly-made")]
    [InlineData("assessment-in-progress", "query-during-assessment")]
    [InlineData("assessment-started", "query-during-assessment")]
    [InlineData("awaiting-decision", "query-during-decision")]
    // Case-insensitive: state ids are compared the same way the engine does.
    [InlineData("SUBMITTED", "query-during-duly-making")]
    public void ResolveQueryActionId_maps_each_queryable_state(string stateId, string expected)
    {
        Assert.Equal(expected, ReAccreditationQueryService.ResolveQueryActionId(stateId));
    }

    [Theory]
    [InlineData("queried")]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    [InlineData("something-else")]
    [InlineData(null)]
    public void ResolveQueryActionId_returns_null_for_a_non_queryable_state(string? stateId)
    {
        Assert.Null(ReAccreditationQueryService.ResolveQueryActionId(stateId));
    }

    // ------------------------------ QueryAsync ------------------------------

    [Theory]
    [InlineData("submitted", "query-during-duly-making")]
    [InlineData("duly-made", "query-during-duly-made")]
    [InlineData("assessment-in-progress", "query-during-assessment")]
    [InlineData("assessment-started", "query-during-assessment")]
    [InlineData("awaiting-decision", "query-during-decision")]
    public async Task QueryAsync_applies_the_action_for_the_current_state(
        string stateId,
        string expectedActionId)
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId);

        var result = await harness.Service.QueryAsync(
            harness.WorkItem.Id, s_sections, "Please clarify", harness.User, ct);

        Assert.True(result.IsSuccess);
        await harness.Engine.Received(1).ApplyActionAsync(
            harness.WorkItem.Id, expectedActionId, Arg.Any<WorkItemActionWrite>(), harness.User, ct);
    }

    [Theory]
    [InlineData("queried")]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    public async Task QueryAsync_fails_with_invalid_transition_from_a_non_queryable_state(
        string stateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId);

        var result = await harness.Service.QueryAsync(
            harness.WorkItem.Id, s_sections, "Please clarify", harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Contains(stateId, result.Message);
        await harness.Engine.DidNotReceiveWithAnyArgs()
            .ApplyActionAsync(default, default!, default(WorkItemActionWrite)!, default!, ct);
        await harness.AuditAppender.DidNotReceiveWithAnyArgs()
            .AppendAsync(default, default!, default!, default!, default!, ct);
    }

    // ---------- RA-291 self-assign + current-query stamp, RA-000 one write ----------

    [Fact]
    public async Task QueryAsync_assigns_the_application_to_the_acting_user_in_the_transitions_write()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("submitted");

        var result = await harness.Service.QueryAsync(
            harness.WorkItem.Id, s_sections, "Please clarify", harness.User, ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(new WorkItemAssignee("alice-1", "Alice Example"), harness.Write!.AssignTo);
        // Never a separate assign: a failed or lost transition must leave no
        // assignment behind.
        await harness.Engine.DidNotReceiveWithAnyArgs()
            .AssignAsync(default, default!, default, default!, ct);
    }

    [Fact]
    public async Task QueryAsync_stamps_the_current_query_in_the_transitions_write()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("submitted");

        await harness.Service.QueryAsync(
            harness.WorkItem.Id, s_sections, "Tonnage does not reconcile", harness.User, ct);

        var write = harness.Write!;
        var stamped = Assert.Single(write.PayloadFields);
        Assert.Equal(ReAccreditationQueryService.CurrentQueryPayloadField, stamped.Key);
        var doc = stamped.Value.AsBsonDocument;
        Assert.Equal("Tonnage does not reconcile", doc["reason"].AsString);
        Assert.Equal("alice-1", doc["raisedBy"].AsString);
        Assert.Equal(
            ["business-plan", "prn-tonnage"],
            doc["sections"].AsBsonArray.Select(v => v.AsString));
        Assert.Equal(s_now.UtcDateTime, doc["raisedAt"].ToUniversalTime());

        // The transition records the stamp's id, which is what the hooks
        // match on (ReAccreditationFreshQuery).
        var queryId = doc["queryId"].AsString;
        Assert.True(Guid.TryParse(queryId, out _));
        Assert.Equal(queryId, write.ActionAppliedDetails[ReAccreditationQueryService.QueryIdAuditDetail]);

        // No write of its own, before or after the transition.
        await harness.Persistence.DidNotReceiveWithAnyArgs()
            .SetPayloadFieldAsync(default, default!, default!, ct);
        await harness.Persistence.DidNotReceiveWithAnyArgs()
            .ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task QueryAsync_gives_every_query_its_own_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("submitted");
        var ids = new List<string?>();
        harness.Engine
            .ApplyActionAsync(
                Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Do<WorkItemActionWrite>(w => ids.Add(w.ActionAppliedDetails["queryId"])),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(WorkItemActionResult.Success(harness.WorkItem));

        await harness.Service.QueryAsync(harness.WorkItem.Id, s_sections, "first", harness.User, ct);
        await harness.Service.QueryAsync(harness.WorkItem.Id, s_sections, "second", harness.User, ct);

        Assert.Equal(2, ids.Distinct().Count());
    }

    [Fact]
    public async Task QueryAsync_returns_missing_actor_identity_without_a_user_id_claim()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("submitted", user: new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("client_id", TenantClientId)], "test")));

        var result = await harness.Service.QueryAsync(
            harness.WorkItem.Id, s_sections, "Please clarify", harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.MissingActorIdentity, result.FailureCode);
        await harness.Engine.DidNotReceiveWithAnyArgs()
            .ApplyActionAsync(default, default!, default(WorkItemActionWrite)!, default!, ct);
    }

    [Fact]
    public async Task QueryAsync_records_the_sections_and_reason_on_the_audit_log()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("assessment-in-progress");

        await harness.Service.QueryAsync(
            harness.WorkItem.Id, s_sections, "Tonnage does not reconcile", harness.User, ct);

        await harness.AuditAppender.Received(1).AppendAsync(
            harness.WorkItem.Id,
            ReAccreditationQueryService.AuditAction,
            ReAccreditationQueryService.AuditActionDisplayName,
            Arg.Is<Dictionary<string, string?>>(d =>
                d["actionId"] == "query-during-assessment"
                && d["queryId"] == harness.Write!.ActionAppliedDetails["queryId"]
                && d["sections"] == "business-plan,prn-tonnage"
                && d["reason"] == "Tonnage does not reconcile"),
            harness.User,
            CancellationToken.None);
    }

    [Fact]
    public async Task QueryAsync_returns_not_found_when_the_work_item_does_not_exist()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("submitted", seedWorkItem: false);

        var result = await harness.Service.QueryAsync(
            harness.WorkItem.Id, s_sections, "Please clarify", harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task QueryAsync_succeeds_for_a_work_item_not_submitted_by_the_caller()
    {
        // RBAC lives in the frontend now (ADR-0005) — the service performs
        // the query regardless of who submitted the item.
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("submitted", submittedBy: "another-tenant");

        var result = await harness.Service.QueryAsync(
            harness.WorkItem.Id, s_sections, "Please clarify", harness.User, ct);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task QueryAsync_rejects_a_work_item_of_a_different_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("submitted", typeId: "some-other-type");

        var result = await harness.Service.QueryAsync(
            harness.WorkItem.Id, s_sections, "Please clarify", harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.UnknownAction, result.FailureCode);
    }

    [Theory]
    // The loser of two overlapping queries: the engine's versioned save
    // failed, or the winner had already moved the item on.
    [InlineData(WorkItemActionFailureCode.ConcurrencyConflict)]
    [InlineData(WorkItemActionFailureCode.InvalidTransition)]
    [InlineData(WorkItemActionFailureCode.WorkItemNotFound)]
    public async Task QueryAsync_propagates_an_engine_failure_without_writing_anything_else(
        WorkItemActionFailureCode failureCode)
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("submitted");
        harness.Engine
            .ApplyActionAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<WorkItemActionWrite>(),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(WorkItemActionResult.Failure(failureCode, "refused"));

        var result = await harness.Service.QueryAsync(
            harness.WorkItem.Id, s_sections, "Please clarify", harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(failureCode, result.FailureCode);
        await harness.AuditAppender.DidNotReceiveWithAnyArgs()
            .AppendAsync(default, default!, default!, default!, default!, ct);
        await harness.Persistence.DidNotReceiveWithAnyArgs()
            .SetPayloadFieldAsync(default, default!, default!, ct);
        await harness.Persistence.DidNotReceiveWithAnyArgs()
            .ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task QueryAsync_still_succeeds_when_the_audit_detail_could_not_be_appended()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("submitted");
        harness.AuditAppender
            .AppendAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(), Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await harness.Service.QueryAsync(
            harness.WorkItem.Id, s_sections, "Please clarify", harness.User, ct);

        // The transition is already persisted; failing the call now would
        // misreport the application's state to the caller.
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task QueryAsync_returns_the_engine_result_when_the_reread_finds_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("submitted");
        // First read finds the item, the post-audit re-read does not (the
        // document was archived/deleted by a concurrent writer).
        harness.Persistence
            .GetByIdAsync(harness.WorkItem.Id, Arg.Any<CancellationToken>())
            .Returns(harness.WorkItem, (WorkItem?)null);

        var result = await harness.Service.QueryAsync(
            harness.WorkItem.Id, s_sections, "Please clarify", harness.User, ct);

        Assert.True(result.IsSuccess);
        Assert.Same(harness.WorkItem, result.WorkItem);
    }

    [Fact]
    public async Task QueryAsync_rejects_null_arguments()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("submitted");

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            harness.Service.QueryAsync(harness.WorkItem.Id, null!, "why", harness.User, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            harness.Service.QueryAsync(harness.WorkItem.Id, s_sections, "why", null!, ct));
    }

    private sealed class Harness
    {
        public Harness(
            string stateId,
            bool seedWorkItem = true,
            string typeId = ReAccreditationType.Id,
            string submittedBy = TenantClientId,
            ClaimsPrincipal? user = null)
        {
            WorkItem = new WorkItem
            {
                TypeId = typeId,
                StateId = stateId,
                SubmittedBy = submittedBy,
            };

            Persistence = Substitute.For<IWorkItemPersistence>();
            Persistence
                .GetByIdAsync(WorkItem.Id, Arg.Any<CancellationToken>())
                .Returns(seedWorkItem ? WorkItem : null);

            Engine = Substitute.For<IWorkItemService>();
            Engine
                .ApplyActionAsync(
                    Arg.Any<Guid>(), Arg.Any<string>(), Arg.Do<WorkItemActionWrite>(w => Write = w),
                    Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
                .Returns(WorkItemActionResult.Success(WorkItem));

            AuditAppender = Substitute.For<IWorkItemAuditAppender>();
            AuditAppender
                .AppendAsync(
                    Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<Dictionary<string, string?>>(), Arg.Any<ClaimsPrincipal>(),
                    Arg.Any<CancellationToken>())
                .Returns(true);

            User = user ?? new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("user:id", "alice-1"),
                    new Claim("user:name", "Alice Example"),
                    new Claim("client_id", TenantClientId),
                ],
                "test"));

            Service = new ReAccreditationQueryService(
                Persistence,
                Engine,
                AuditAppender,
                NullLogger<ReAccreditationQueryService>.Instance,
                new FakeTimeProvider(s_now));
        }

        public WorkItem WorkItem { get; }
        public IWorkItemPersistence Persistence { get; }
        public IWorkItemService Engine { get; }

        /// <summary>The write the service last handed the engine.</summary>
        public WorkItemActionWrite? Write { get; private set; }
        public IWorkItemAuditAppender AuditAppender { get; }
        public ClaimsPrincipal User { get; }
        public ReAccreditationQueryService Service { get; }
    }
}
