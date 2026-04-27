using System.Security.Claims;
using Backend.Api.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Backend.Api.Test.WorkItems.Core;

public class WorkItemServiceTests
{
    private const string TypeId = "test-type";
    private static readonly DateTime InitialNow = new(2026, 4, 27, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TickedNow = InitialNow.AddMinutes(5);

    private readonly IWorkItemPersistence _persistence = Substitute.For<IWorkItemPersistence>();
    private readonly FakeTimeProvider _time = new(TickedNow);

    private WorkItemService BuildService(IWorkItemType type) =>
        new(
            new WorkItemRegistry([type]),
            _persistence,
            NullLogger<WorkItemService>.Instance,
            _time);

    private static TestWorkItemType BuildType(
        WorkItemTransition[]? transitions = null,
        Dictionary<string, IReadOnlyCollection<WorkItemTask>>? tasksByState = null)
    {
        var states = new[]
        {
            new WorkItemState("submitted", "Submitted"),
            new WorkItemState("approved", "Approved", IsTerminal: true),
            new WorkItemState("rejected", "Rejected", IsTerminal: true)
        };
        return new TestWorkItemType(
            TypeId,
            "Test type",
            initialState: states[0],
            states: states,
            tasksByState: tasksByState,
            transitions: transitions);
    }

    private WorkItem ExistingWorkItem(string stateId = "submitted", Dictionary<string, HashSet<string>>? completed = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            TypeId = TypeId,
            StateId = stateId,
            SubmittedAt = InitialNow,
            LastModifiedAt = InitialNow,
            SubmittedBy = "test-client",
            CompletedTaskIdsByState = completed ?? new()
        };

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity([new Claim("cognito:client_id", "test-client")], "test"));

    [Fact]
    public async Task CompleteTask_records_task_against_current_state_and_persists()
    {
        var type = BuildType(tasksByState: new()
        {
            ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
        });
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(type).CompleteTaskAsync(
            workItem.Id, "check-eligibility", User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Contains("check-eligibility", workItem.CompletedTaskIdsByState["submitted"]);
        Assert.Equal(TickedNow, workItem.LastModifiedAt);
        await _persistence.Received(1).ReplaceAsync(workItem, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteTask_is_idempotent_when_already_complete()
    {
        var type = BuildType(tasksByState: new()
        {
            ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
        });
        var workItem = ExistingWorkItem(completed: new()
        {
            ["submitted"] = ["check-eligibility"]
        });
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(type).CompleteTaskAsync(
            workItem.Id, "check-eligibility", User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(InitialNow, workItem.LastModifiedAt);
        await _persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteTask_fails_when_task_does_not_apply_to_current_state()
    {
        var type = BuildType(tasksByState: new()
        {
            ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
        });
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(type).CompleteTaskAsync(
            workItem.Id, "unknown-task", User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.TaskNotApplicable, result.FailureCode);
        await _persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteTask_returns_not_found_when_work_item_missing()
    {
        var type = BuildType();
        _persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var result = await BuildService(type).CompleteTaskAsync(
            Guid.NewGuid(), "any", User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task ApplyAction_blocks_approve_while_tasks_outstanding()
    {
        var type = BuildType(
            tasksByState: new()
            {
                ["submitted"] = [
                    new WorkItemTask("check-eligibility", "Check eligibility"),
                    new WorkItemTask("verify-documents", "Verify documents")
                ]
            },
            transitions: [
                new WorkItemTransition("approve", "Approve", "submitted", "approved")
            ]);
        var workItem = ExistingWorkItem(completed: new()
        {
            ["submitted"] = ["check-eligibility"]
        });
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(type).ApplyActionAsync(
            workItem.Id, "approve", User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.IncompleteTasks, result.FailureCode);
        Assert.Equal("submitted", workItem.StateId);
        await _persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAction_transitions_when_all_tasks_complete()
    {
        var type = BuildType(
            tasksByState: new()
            {
                ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
            },
            transitions: [
                new WorkItemTransition("approve", "Approve", "submitted", "approved")
            ]);
        var workItem = ExistingWorkItem(completed: new()
        {
            ["submitted"] = ["check-eligibility"]
        });
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(type).ApplyActionAsync(
            workItem.Id, "approve", User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("approved", workItem.StateId);
        Assert.Equal(TickedNow, workItem.LastModifiedAt);
        await _persistence.Received(1).ReplaceAsync(workItem, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAction_allows_action_that_does_not_require_task_completion()
    {
        var type = BuildType(
            tasksByState: new()
            {
                ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
            },
            transitions: [
                new WorkItemTransition("withdraw", "Withdraw", "submitted", "rejected", RequiresAllTasksComplete: false)
            ]);
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(type).ApplyActionAsync(
            workItem.Id, "withdraw", User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("rejected", workItem.StateId);
    }

    [Fact]
    public async Task ApplyAction_fails_when_action_does_not_apply_to_current_state()
    {
        var type = BuildType(transitions: [
            new WorkItemTransition("approve", "Approve", "submitted", "approved")
        ]);
        var workItem = ExistingWorkItem(stateId: "approved");
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(type).ApplyActionAsync(
            workItem.Id, "approve", User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.TerminalState, result.FailureCode);
    }

    [Fact]
    public async Task ApplyAction_fails_when_action_unknown()
    {
        var type = BuildType(transitions: [
            new WorkItemTransition("approve", "Approve", "submitted", "approved")
        ]);
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(type).ApplyActionAsync(
            workItem.Id, "delete", User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.UnknownAction, result.FailureCode);
    }

    [Fact]
    public void Project_lists_only_actions_whose_preconditions_are_met()
    {
        var type = BuildType(
            tasksByState: new()
            {
                ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
            },
            transitions: [
                new WorkItemTransition("approve", "Approve", "submitted", "approved"),
                new WorkItemTransition("reject", "Reject", "submitted", "rejected"),
                new WorkItemTransition("withdraw", "Withdraw", "submitted", "rejected", RequiresAllTasksComplete: false)
            ]);
        var workItem = ExistingWorkItem();

        var projection = BuildService(type).Project(workItem);

        Assert.Single(projection.Tasks);
        Assert.False(projection.Tasks.Single().IsComplete);

        // Approve and reject are gated; withdraw is always available.
        Assert.Equal(["withdraw"], projection.AvailableActions.Select(a => a.ActionId).ToArray());
    }

    [Fact]
    public void Project_returns_no_actions_for_terminal_state()
    {
        var type = BuildType(transitions: [
            new WorkItemTransition("approve", "Approve", "submitted", "approved")
        ]);
        var workItem = ExistingWorkItem(stateId: "approved");

        var projection = BuildService(type).Project(workItem);

        Assert.Empty(projection.AvailableActions);
        Assert.Empty(projection.Tasks);
    }

    private sealed class FakeTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
