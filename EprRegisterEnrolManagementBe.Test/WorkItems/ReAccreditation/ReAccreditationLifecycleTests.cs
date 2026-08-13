using System.Security.Claims;
using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.Utils.Background;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// Cheap proof that the re-accreditation module integrates with the framework
/// engine: walk a freshly-submitted work item from <c>submitted</c> through
/// to <c>approved</c> by applying the declared actions, then assert the
/// framework's audit log captured each step.
///
/// RA-410: this used to also complete every state's task checklist along the
/// way (hence the class's original name for the walk-through fact below) —
/// the task framework is gone, so every state simply has no checklist to
/// complete and the walk is that much shorter.
/// </summary>
public class ReAccreditationLifecycleTests
{
    [Fact]
    public async Task Walk_from_submitted_to_approved_records_full_audit_trail()
    {
        var ct = TestContext.Current.CancellationToken;
        var type = new ReAccreditationType();
        var persistence = Substitute.For<IWorkItemPersistence>();
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-id"));
        var pushAdapter = Substitute.For<IOperatorBackendPushAdapter>();
        pushAdapter
            .PushStatusChangedAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(OperatorBackendPushResult.Skipped("test"));
        var statusPushHook = new ReAccreditationStatusPushHook(
            pushAdapter,
            auditAppender,
            NullLogger<ReAccreditationStatusPushHook>.Instance
        );
        // RA-316: duly making is an explicit regulator action carrying a payment
        // date, not a side effect of ticking a checklist, so it is driven here
        // by its own service exactly as the walk-through would drive it.
        var dulyMakingService = new ReAccreditationDulyMakingService(
            persistence,
            new WorkItemRegistry([type]),
            [statusPushHook],
            TimeProvider.System,
            NullLogger<ReAccreditationDulyMakingService>.Instance
        );
        var engine = new WorkItemService(
            new WorkItemRegistry([type]),
            persistence,
            NullLogger<WorkItemService>.Instance
        );
        var idGenerator = Substitute.For<IAccreditationIdGenerator>();
        idGenerator
            .GenerateAsync(Arg.Any<BsonDocument>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns("ACC-2027-X-TEST0000");
        var approvalService = new ReAccreditationApprovalService(
            persistence,
            new WorkItemRegistry([type]),
            idGenerator,
            Substitute.For<IBackgroundTaskQueue>(),
            [],
            NullLogger<ReAccreditationApprovalService>.Instance,
            Options.Create(new AccreditationConfig { CurrentYear = 2027 })
        );

        const string tenantClientId = "test-client";
        var workItem = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = type.InitialState.Id,
            SubmittedBy = tenantClientId,
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion,
        };
        persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var user = new ClaimsPrincipal(
            new ClaimsIdentity(
                [
                    new Claim("user:id", "alice-1"),
                    new Claim("user:name", "Alice Example"),
                    new Claim("client_id", tenantClientId),
                    new Claim(ClaimTypes.Role, "reaccreditation-decision-maker"),
                ],
                "test"
            )
        );

        // RA-316: the regulator presses "Duly make" and supplies the payment
        // date the SLA clock is anchored to, which is the only way out of
        // 'submitted'. RA-410: there is no task framework left to check.
        var paymentDate = new DateOnly(2027, 1, 20);
        Assert.True(
            (
                await dulyMakingService.CompleteDulyMakingAsync(workItem.Id, paymentDate, user, ct)
            ).IsSuccess
        );
        Assert.Equal("duly-made", workItem.StateId);
        Assert.Equal(
            paymentDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            workItem.SlaClock!.StartedAt
        );

        Assert.True(
            (await engine.ApplyActionAsync(workItem.Id, "payment-received", user, ct)).IsSuccess
        );

        Assert.True(
            (await engine.ApplyActionAsync(workItem.Id, "submit-for-decision", user, ct)).IsSuccess
        );

        // RA-132: approve goes through the bespoke ReAccreditationApprovalService,
        // not the generic engine — the generic engine no longer registers the
        // approve transition so it cannot silently produce an item missing the
        // accreditation id, SLA clock and audit entries.
        Assert.True((await approvalService.ApproveAsync(workItem.Id, user, ct)).IsSuccess);

        Assert.Equal("approved", workItem.StateId);

        // RA-410: no state contributes task-completed entries any more — the
        // task framework is gone, so the walk is shorter than it used to be.
        // 1 action-applied:duly-make + 1 sla-clock-started
        // + 1 action-applied:payment-received + 1 action-applied:submit-for-decision
        // + 3 approval entries (action-applied, sla-clock-stopped, accreditation-issued) = 7 total.
        Assert.Equal(7, workItem.AuditLog.Count);
        Assert.Contains(
            workItem.AuditLog,
            e =>
                e.Action == "action-applied"
                && e.Details.GetValueOrDefault("actionId") == "duly-make"
        );
        Assert.Contains(
            workItem.AuditLog,
            e =>
                e.Action == "action-applied"
                && e.Details.GetValueOrDefault("actionId") == "approve"
                && e.Details.GetValueOrDefault("toStateId") == "approved"
        );
        Assert.All(
            workItem.AuditLog,
            entry =>
            {
                Assert.Equal("alice-1", entry.CreatedBy);
                Assert.Equal("Alice Example", entry.CreatedByName);
            }
        );
    }

    [Fact]
    public async Task Withdraw_from_submitted_bypasses_task_completion_gate()
    {
        var ct = TestContext.Current.CancellationToken;
        var type = new ReAccreditationType();
        var persistence = Substitute.For<IWorkItemPersistence>();
        var engine = new WorkItemService(
            new WorkItemRegistry([type]),
            persistence,
            NullLogger<WorkItemService>.Instance
        );

        var workItem = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = type.InitialState.Id,
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion,
        };
        persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        // RA-410: withdraw succeeds unconditionally now — there is no task
        // framework left to gate it.
        var user = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("user:id", "alice-1")], "test")
        );
        var result = await engine.ApplyActionAsync(workItem.Id, "withdraw", user, ct);

        Assert.True(result.IsSuccess);
        Assert.Equal("withdrawn", workItem.StateId);
    }

    [Fact]
    public async Task Withdraw_from_awaiting_decision_is_allowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var type = new ReAccreditationType();
        var persistence = Substitute.For<IWorkItemPersistence>();
        var engine = new WorkItemService(
            new WorkItemRegistry([type]),
            persistence,
            NullLogger<WorkItemService>.Instance
        );

        var workItem = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "awaiting-decision",
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion,
        };
        persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var user = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("user:id", "alice-1")], "test")
        );
        var result = await engine.ApplyActionAsync(
            workItem.Id,
            "withdraw-during-decision",
            user,
            ct
        );

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("withdrawn", workItem.StateId);
    }

    [Fact]
    public async Task Withdraw_from_queried_is_allowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var type = new ReAccreditationType();
        var persistence = Substitute.For<IWorkItemPersistence>();
        var engine = new WorkItemService(
            new WorkItemRegistry([type]),
            persistence,
            NullLogger<WorkItemService>.Instance
        );

        var workItem = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "queried",
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion,
        };
        persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var user = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("user:id", "alice-1")], "test")
        );
        var result = await engine.ApplyActionAsync(workItem.Id, "withdraw-during-query", user, ct);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("withdrawn", workItem.StateId);
    }

    [Fact]
    public async Task Withdraw_from_updated_is_allowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var type = new ReAccreditationType();
        var persistence = Substitute.For<IWorkItemPersistence>();
        var engine = new WorkItemService(
            new WorkItemRegistry([type]),
            persistence,
            NullLogger<WorkItemService>.Instance
        );

        var workItem = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "updated",
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion,
        };
        persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var user = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("user:id", "alice-1")], "test")
        );
        var result = await engine.ApplyActionAsync(
            workItem.Id,
            "withdraw-during-updated",
            user,
            ct
        );

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("withdrawn", workItem.StateId);
    }

    [Fact]
    public async Task Query_from_assessment_in_progress_is_allowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var type = new ReAccreditationType();
        var persistence = Substitute.For<IWorkItemPersistence>();
        var engine = new WorkItemService(
            new WorkItemRegistry([type]),
            persistence,
            NullLogger<WorkItemService>.Instance
        );

        var workItem = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "assessment-in-progress",
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion,
        };
        persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        // RA-410: query succeeds unconditionally now — there is no task
        // framework left to gate it.
        var user = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("user:id", "alice-1")], "test")
        );
        var result = await engine.ApplyActionAsync(
            workItem.Id,
            "query-during-assessment",
            user,
            ct
        );

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("queried", workItem.StateId);
    }

    [Fact]
    public async Task Query_from_awaiting_decision_is_allowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var type = new ReAccreditationType();
        var persistence = Substitute.For<IWorkItemPersistence>();
        var engine = new WorkItemService(
            new WorkItemRegistry([type]),
            persistence,
            NullLogger<WorkItemService>.Instance
        );

        var workItem = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "awaiting-decision",
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion,
        };
        persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var user = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("user:id", "alice-1")], "test")
        );
        var result = await engine.ApplyActionAsync(workItem.Id, "query-during-decision", user, ct);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("queried", workItem.StateId);
    }

    [Theory]
    [InlineData("approved", "query-during-decision")]
    [InlineData("rejected", "query-during-decision")]
    [InlineData("withdrawn", "query-during-decision")]
    public async Task Query_is_rejected_when_work_item_is_in_a_terminal_state(
        string terminalStateId,
        string actionId
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var type = new ReAccreditationType();
        var persistence = Substitute.For<IWorkItemPersistence>();
        var engine = new WorkItemService(
            new WorkItemRegistry([type]),
            persistence,
            NullLogger<WorkItemService>.Instance
        );

        var workItem = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = terminalStateId,
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion,
        };
        persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var user = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("user:id", "alice-1")], "test")
        );
        var result = await engine.ApplyActionAsync(workItem.Id, actionId, user, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.TerminalState, result.FailureCode);
        Assert.Equal(terminalStateId, workItem.StateId);
    }

    /// <summary>
    /// RA-291: a query raised against the real engine (not a substituted
    /// one) moves the item to <c>queried</c> from every pre-decision state
    /// without any task having to be completed first, and a second query
    /// against the now-queried item is refused.
    /// </summary>
    [Theory]
    [InlineData("submitted", "query-during-duly-making")]
    [InlineData("duly-made", "query-during-duly-made")]
    [InlineData("assessment-in-progress", "query-during-assessment")]
    [InlineData("awaiting-decision", "query-during-decision")]
    public async Task Query_moves_the_item_to_queried_from_any_pre_decision_state(
        string stateId,
        string expectedActionId
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var type = new ReAccreditationType();
        var persistence = Substitute.For<IWorkItemPersistence>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        auditAppender
            .AppendAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);
        var engine = new WorkItemService(
            new WorkItemRegistry([type]),
            persistence,
            NullLogger<WorkItemService>.Instance
        );
        // The query stamps the open query with a targeted payload write
        // before transitioning; the substitute must report a match.
        persistence
            .SetPayloadFieldAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<BsonValue>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);
        var queryService = new ReAccreditationQueryService(
            persistence,
            engine,
            auditAppender,
            NullLogger<ReAccreditationQueryService>.Instance
        );

        const string tenantClientId = "test-client";
        var workItem = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            SubmittedBy = tenantClientId,
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion,
        };
        persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var user = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim("user:id", "alice-1"), new Claim("client_id", tenantClientId)],
                "test"
            )
        );

        string[] sections = ["business-plan"];
        var result = await queryService.QueryAsync(
            workItem.Id,
            sections,
            "Please confirm the tonnage figures.",
            user,
            ct
        );

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("queried", workItem.StateId);
        // RA-410: no task framework is left to gate the query transitions.
        Assert.Contains(
            workItem.AuditLog,
            e =>
                e.Action == "action-applied"
                && e.Details.GetValueOrDefault("actionId") == expectedActionId
        );

        // An application awaiting a response cannot be queried again — the
        // state machine has no transition out of 'queried', and the service
        // reports it as an invalid transition (409) rather than blowing up.
        var second = await queryService.QueryAsync(
            workItem.Id,
            sections,
            "Please confirm the tonnage figures.",
            user,
            ct
        );

        Assert.False(second.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, second.FailureCode);
        Assert.Equal("queried", workItem.StateId);
    }
}
