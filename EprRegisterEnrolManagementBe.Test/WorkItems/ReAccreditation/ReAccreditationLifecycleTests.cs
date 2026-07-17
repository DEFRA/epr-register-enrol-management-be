using System.Security.Claims;
using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.Utils.Background;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// Cheap proof that the re-accreditation module integrates with the framework
/// engine: walk a freshly-submitted work item from <c>submitted</c> through
/// to <c>approved</c> by completing every task and applying the declared
/// actions, then assert the framework's audit log captured each step.
/// </summary>
public class ReAccreditationLifecycleTests
{
    [Fact]
    public async Task Walk_from_submitted_to_approved_completing_every_task_records_full_audit_trail()
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
        var dulyMadeHook = new ReAccreditationDulyMadeHook(
            persistence,
            notifyClient,
            auditAppender,
            TimeProvider.System,
            NullLogger<ReAccreditationDulyMadeHook>.Instance
        );
        var engine = new WorkItemService(
            new WorkItemRegistry([type]),
            persistence,
            NullLogger<WorkItemService>.Instance,
            postTaskHooks: [dulyMadeHook]
        );
        var idGenerator = Substitute.For<IAccreditationIdGenerator>();
        idGenerator
            .GenerateAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns("ACC-2027-X-TEST0000");
        var approvalService = new ReAccreditationApprovalService(
            persistence,
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
                    new Claim("cognito:client_id", tenantClientId),
                    new Claim(ClaimTypes.Role, "reaccreditation-decision-maker"),
                ],
                "test"
            )
        );

        // Completing all submitted-state tasks auto-triggers the hook which
        // transitions the item to duly-made (no separate action call needed).
        await CompleteAll(engine, workItem.Id, type, "submitted", user, ct);
        Assert.Equal("duly-made", workItem.StateId);

        await CompleteAll(engine, workItem.Id, type, "duly-made", user, ct);
        Assert.True(
            (await engine.ApplyActionAsync(workItem.Id, "payment-received", user, ct)).IsSuccess
        );

        await CompleteAll(engine, workItem.Id, type, "assessment-in-progress", user, ct);
        Assert.True(
            (await engine.ApplyActionAsync(workItem.Id, "submit-for-decision", user, ct)).IsSuccess
        );

        await CompleteAll(engine, workItem.Id, type, "awaiting-decision", user, ct);
        // RA-132: approve goes through the bespoke ReAccreditationApprovalService,
        // not the generic engine — the generic engine no longer registers the
        // approve transition so it cannot silently produce an item missing the
        // accreditation id, SLA clock and audit entries.
        Assert.True((await approvalService.ApproveAsync(workItem.Id, user, ct)).IsSuccess);

        Assert.Equal("approved", workItem.StateId);

        // 2 task-completed (submitted) + 1 action-applied:duly-make + 1 sla-clock-started (hook)
        // + 1 task-completed (duly-made) + 1 action-applied:payment-received
        // + 3 task-completed (assessment-in-progress) + 1 action-applied:submit-for-decision
        // + 1 task-completed (awaiting-decision) + 3 approval entries = 14 total.
        Assert.Equal(14, workItem.AuditLog.Count);
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

        // Do not complete any tasks; withdraw should still succeed because
        // the transition declares RequiresAllTasksComplete: false.
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

        // Do not complete any tasks; query should still succeed because the
        // transition declares RequiresAllTasksComplete: false.
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

    private static async Task CompleteAll(
        IWorkItemService engine,
        Guid id,
        ReAccreditationType type,
        string stateId,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        foreach (var task in type.GetTasksForState(stateId))
        {
            var result = await engine.CompleteTaskAsync(id, task.Id, user, ct);
            Assert.True(result.IsSuccess, $"completing {task.Id} in {stateId}: {result.Message}");
        }
    }
}
