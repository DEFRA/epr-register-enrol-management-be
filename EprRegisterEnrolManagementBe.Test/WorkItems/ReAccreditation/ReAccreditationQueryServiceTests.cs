using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-291: the query service resolves the right <c>query-during-*</c> action
/// from the work item's current state, delegates the state change to the
/// framework engine, and records the queried sections + reason on the audit
/// log.
/// </summary>
public class ReAccreditationQueryServiceTests
{
    private const string TenantClientId = "test-client";

    private static readonly string[] s_sections = ["business-plan", "prn-tonnage"];

    // -------------------------- action resolution --------------------------

    [Theory]
    [InlineData("submitted", "query-during-duly-making")]
    [InlineData("duly-made", "query-during-duly-made")]
    [InlineData("assessment-in-progress", "query-during-assessment")]
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
            harness.WorkItem.Id, expectedActionId, harness.User, ct);
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
            .ApplyActionAsync(default, default!, default!, default);
        await harness.AuditAppender.DidNotReceiveWithAnyArgs()
            .AppendAsync(default, default!, default!, default!, default!, default);
    }

    // ------------------------- RA-291 self-assignment -------------------------

    [Fact]
    public async Task QueryAsync_assigns_the_application_to_the_acting_user_before_transitioning()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("submitted");

        var result = await harness.Service.QueryAsync(
            harness.WorkItem.Id, s_sections, "Please clarify", harness.User, ct);

        Assert.True(result.IsSuccess);
        await harness.Engine.Received(1).AssignAsync(
            harness.WorkItem.Id, "alice-1", "Alice Example", harness.User, ct);

        // Ordering matters: a failed assign must leave the application
        // un-queried, never queried-but-unassigned.
        Received.InOrder(() =>
        {
            harness.Engine.AssignAsync(
                harness.WorkItem.Id, "alice-1", "Alice Example", harness.User, ct);
            harness.Engine.ApplyActionAsync(
                harness.WorkItem.Id, "query-during-duly-making", harness.User, ct);
        });
    }

    [Fact]
    public async Task QueryAsync_treats_an_already_assigned_to_me_item_as_a_clean_no_op()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("submitted");
        // The engine reports an idempotent replay when the item is already
        // assigned to the same user: success, but nothing written and no
        // duplicate audit entry / notification.
        harness.Engine
            .AssignAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(WorkItemActionResult.IdempotentReplay(harness.WorkItem));

        var result = await harness.Service.QueryAsync(
            harness.WorkItem.Id, s_sections, "Please clarify", harness.User, ct);

        Assert.True(result.IsSuccess);
        // The query itself still proceeds — the no-op is only the assign half.
        await harness.Engine.Received(1).ApplyActionAsync(
            harness.WorkItem.Id, "query-during-duly-making", harness.User, ct);
    }

    [Fact]
    public async Task QueryAsync_aborts_without_transitioning_when_the_assignment_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("submitted");
        harness.Engine
            .AssignAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(WorkItemActionResult.Failure(
                WorkItemActionFailureCode.NotAuthorized,
                "This work item is already assigned to another user; only users with the 'assign' role can re-assign it."));

        var result = await harness.Service.QueryAsync(
            harness.WorkItem.Id, s_sections, "Please clarify", harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.NotAuthorized, result.FailureCode);
        Assert.Equal("submitted", harness.WorkItem.StateId);
        await harness.Engine.DidNotReceiveWithAnyArgs()
            .ApplyActionAsync(default, default!, default!, default);
        await harness.AuditAppender.DidNotReceiveWithAnyArgs()
            .AppendAsync(default, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task QueryAsync_returns_missing_actor_identity_without_a_user_id_claim()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("submitted", user: new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("cognito:client_id", TenantClientId)], "test")));

        var result = await harness.Service.QueryAsync(
            harness.WorkItem.Id, s_sections, "Please clarify", harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.MissingActorIdentity, result.FailureCode);
        await harness.Engine.DidNotReceiveWithAnyArgs()
            .AssignAsync(default, default!, default, default!, default);
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
                && d["sections"] == "business-plan,prn-tonnage"
                && d["reason"] == "Tonnage does not reconcile"),
            harness.User,
            ct);
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
    public async Task QueryAsync_returns_not_found_when_the_caller_cannot_read_the_work_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("submitted", submittedBy: "another-tenant");

        var result = await harness.Service.QueryAsync(
            harness.WorkItem.Id, s_sections, "Please clarify", harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
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

    [Fact]
    public async Task QueryAsync_propagates_an_engine_failure_without_writing_audit_detail()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("submitted");
        harness.Engine
            .ApplyActionAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>())
            .Returns(WorkItemActionResult.Failure(
                WorkItemActionFailureCode.MissingActorIdentity, "no user"));

        var result = await harness.Service.QueryAsync(
            harness.WorkItem.Id, s_sections, "Please clarify", harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.MissingActorIdentity, result.FailureCode);
        await harness.AuditAppender.DidNotReceiveWithAnyArgs()
            .AppendAsync(default, default!, default!, default!, default!, default);
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
                .AssignAsync(
                    Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
                .Returns(WorkItemActionResult.Success(WorkItem));
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

            User = user ?? new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("user:id", "alice-1"),
                    new Claim("user:name", "Alice Example"),
                    new Claim("cognito:client_id", TenantClientId),
                ],
                "test"));

            Service = new ReAccreditationQueryService(
                Persistence,
                Engine,
                AuditAppender,
                NullLogger<ReAccreditationQueryService>.Instance);
        }

        public WorkItem WorkItem { get; }
        public IWorkItemPersistence Persistence { get; }
        public IWorkItemService Engine { get; }
        public IWorkItemAuditAppender AuditAppender { get; }
        public ClaimsPrincipal User { get; }
        public ReAccreditationQueryService Service { get; }
    }
}
