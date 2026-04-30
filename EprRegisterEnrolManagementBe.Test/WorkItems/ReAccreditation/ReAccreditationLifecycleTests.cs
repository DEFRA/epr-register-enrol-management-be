using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
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
        var engine = new WorkItemService(
            new WorkItemRegistry([type]), persistence, NullLogger<WorkItemService>.Instance);

        var workItem = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = type.InitialState.Id,
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion
        };
        persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("user:id", "alice-1"),
            new Claim("user:name", "Alice Example"),
            new Claim(ClaimTypes.Role, ReAccreditationType.DecisionMakerRole)
        ], "test"));

        // Per-state task completion → action.
        await CompleteAll(engine, workItem.Id, type, "submitted", user, ct);
        Assert.True((await engine.ApplyActionAsync(workItem.Id, "start-assessment", user, ct)).IsSuccess);

        await CompleteAll(engine, workItem.Id, type, "assessment-in-progress", user, ct);
        Assert.True((await engine.ApplyActionAsync(workItem.Id, "submit-for-decision", user, ct)).IsSuccess);

        await CompleteAll(engine, workItem.Id, type, "awaiting-decision", user, ct);
        Assert.True((await engine.ApplyActionAsync(workItem.Id, "approve", user, ct)).IsSuccess);

        Assert.Equal("approved", workItem.StateId);

        // 6 task completions + 3 transitions = 9 audit entries.
        Assert.Equal(9, workItem.AuditLog.Count);
        Assert.Contains(workItem.AuditLog, e => e.Action == "action-applied"
            && e.Details.GetValueOrDefault("actionId") == "start-assessment");
        Assert.Contains(workItem.AuditLog, e => e.Action == "action-applied"
            && e.Details.GetValueOrDefault("actionId") == "approve"
            && e.Details.GetValueOrDefault("toStateId") == "approved");
        Assert.All(workItem.AuditLog, entry =>
        {
            Assert.Equal("alice-1", entry.CreatedBy);
            Assert.Equal("Alice Example", entry.CreatedByName);
        });
    }

    [Fact]
    public async Task Withdraw_from_submitted_bypasses_task_completion_gate()
    {
        var ct = TestContext.Current.CancellationToken;
        var type = new ReAccreditationType();
        var persistence = Substitute.For<IWorkItemPersistence>();
        var engine = new WorkItemService(
            new WorkItemRegistry([type]), persistence, NullLogger<WorkItemService>.Instance);

        var workItem = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = type.InitialState.Id,
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion
        };
        persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        // Do not complete any tasks; withdraw should still succeed because
        // the transition declares RequiresAllTasksComplete: false.
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("user:id", "alice-1")], "test"));
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
            new WorkItemRegistry([type]), persistence, NullLogger<WorkItemService>.Instance);

        var workItem = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "awaiting-decision",
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion
        };
        persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("user:id", "alice-1")], "test"));
        var result = await engine.ApplyActionAsync(
            workItem.Id, "withdraw-during-decision", user, ct);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("withdrawn", workItem.StateId);
    }

    private static async Task CompleteAll(
        IWorkItemService engine, Guid id, ReAccreditationType type, string stateId,
        ClaimsPrincipal user, CancellationToken ct)
    {
        foreach (var task in type.GetTasksForState(stateId))
        {
            var result = await engine.CompleteTaskAsync(id, task.Id, user, ct);
            Assert.True(result.IsSuccess, $"completing {task.Id} in {stateId}: {result.Message}");
        }
    }
}