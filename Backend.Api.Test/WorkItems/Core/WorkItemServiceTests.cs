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

    private static ClaimsPrincipal UserWithRoles(string userId, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("cognito:client_id", "test-client"),
            new("user:id", userId)
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

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

    // ---------------------- Assignment ----------------------

    [Fact]
    public async Task Assign_records_assignee_with_snapshot_and_audit_metadata()
    {
        var type = BuildType();
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var actor = UserWithRoles("actor-1", WorkItemService.AssignRole);
        var result = await BuildService(type).AssignAsync(
            workItem.Id, "alice-1", "Alice Example", actor, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("alice-1", workItem.AssignedToId);
        Assert.Equal("Alice Example", workItem.AssignedToName);
        Assert.Equal(TickedNow, workItem.AssignedAt);
        Assert.Equal("actor-1", workItem.AssignedBy);
        Assert.Equal(TickedNow, workItem.LastModifiedAt);
        await _persistence.Received(1).ReplaceAsync(workItem, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Assign_re_assignment_replaces_previous_assignee()
    {
        var type = BuildType();
        var workItem = ExistingWorkItem();
        workItem.AssignedToId = "bob-1";
        workItem.AssignedToName = "Bob";
        workItem.AssignedAt = InitialNow;
        workItem.AssignedBy = "old-actor";
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var actor = UserWithRoles("actor-1", WorkItemService.AssignRole);
        var result = await BuildService(type).AssignAsync(
            workItem.Id, "carol-1", "Carol", actor, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("carol-1", workItem.AssignedToId);
        Assert.Equal("Carol", workItem.AssignedToName);
        Assert.Equal("actor-1", workItem.AssignedBy);
    }

    [Fact]
    public async Task Assign_is_idempotent_when_assignee_unchanged()
    {
        var type = BuildType();
        var workItem = ExistingWorkItem();
        workItem.AssignedToId = "alice-1";
        workItem.AssignedToName = "Alice";
        workItem.AssignedAt = InitialNow;
        workItem.AssignedBy = "old-actor";
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var actor = UserWithRoles("actor-1", WorkItemService.AssignRole);
        var result = await BuildService(type).AssignAsync(
            workItem.Id, "alice-1", "Alice", actor, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(InitialNow, workItem.AssignedAt);
        Assert.Equal("old-actor", workItem.AssignedBy);
        await _persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Assign_blank_assignee_id_is_rejected()
    {
        var type = BuildType();
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var actor = UserWithRoles("actor-1", WorkItemService.AssignRole);
        var result = await BuildService(type).AssignAsync(
            workItem.Id, "   ", null, actor, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidAssignment, result.FailureCode);
    }

    [Fact]
    public async Task Assign_returns_not_found_when_work_item_missing()
    {
        var type = BuildType();
        _persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var actor = UserWithRoles("actor-1", WorkItemService.AssignRole);
        var result = await BuildService(type).AssignAsync(
            Guid.NewGuid(), "alice-1", "Alice", actor, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task Assign_standard_user_can_self_assign_unassigned_item()
    {
        var type = BuildType();
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var actor = UserWithRoles("alice-1", "standard");
        var result = await BuildService(type).AssignAsync(
            workItem.Id, "alice-1", "Alice", actor, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("alice-1", workItem.AssignedToId);
    }

    [Fact]
    public async Task Assign_standard_user_cannot_assign_to_someone_else()
    {
        var type = BuildType();
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var actor = UserWithRoles("alice-1", "standard");
        var result = await BuildService(type).AssignAsync(
            workItem.Id, "bob-1", "Bob", actor, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.NotAuthorized, result.FailureCode);
        await _persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Assign_standard_user_cannot_take_item_already_assigned_to_another_user()
    {
        var type = BuildType();
        var workItem = ExistingWorkItem();
        workItem.AssignedToId = "bob-1";
        workItem.AssignedToName = "Bob";
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var actor = UserWithRoles("alice-1", "standard");
        var result = await BuildService(type).AssignAsync(
            workItem.Id, "alice-1", "Alice", actor, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.NotAuthorized, result.FailureCode);
        Assert.Equal("bob-1", workItem.AssignedToId);
    }

    [Fact]
    public async Task Unassign_clears_assignment_when_actor_has_assign_role()
    {
        var type = BuildType();
        var workItem = ExistingWorkItem();
        workItem.AssignedToId = "alice-1";
        workItem.AssignedToName = "Alice";
        workItem.AssignedAt = InitialNow;
        workItem.AssignedBy = "actor-1";
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var actor = UserWithRoles("actor-2", WorkItemService.AssignRole);
        var result = await BuildService(type).UnassignAsync(workItem.Id, actor, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(workItem.AssignedToId);
        Assert.Null(workItem.AssignedToName);
        Assert.Null(workItem.AssignedAt);
        Assert.Null(workItem.AssignedBy);
        Assert.Equal(TickedNow, workItem.LastModifiedAt);
        await _persistence.Received(1).ReplaceAsync(workItem, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unassign_is_idempotent_for_already_unassigned_item()
    {
        var type = BuildType();
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var actor = UserWithRoles("actor-1", WorkItemService.AssignRole);
        var result = await BuildService(type).UnassignAsync(workItem.Id, actor, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        await _persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unassign_rejected_for_standard_user()
    {
        var type = BuildType();
        var workItem = ExistingWorkItem();
        workItem.AssignedToId = "alice-1";
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var actor = UserWithRoles("alice-1", "standard");
        var result = await BuildService(type).UnassignAsync(workItem.Id, actor, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.NotAuthorized, result.FailureCode);
        Assert.Equal("alice-1", workItem.AssignedToId);
        await _persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddNote_appends_note_with_author_snapshot_and_persists()
    {
        var type = BuildType();
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var actor = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("cognito:client_id", "test-client"),
            new Claim("user:id", "alice-1"),
            new Claim("user:name", "Alice Example")
        ], "test"));

        var result = await BuildService(type).AddNoteAsync(
            workItem.Id, "  Spoke to applicant; awaiting evidence.  ", actor, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var note = Assert.Single(workItem.Notes);
        Assert.Equal("Spoke to applicant; awaiting evidence.", note.Text);
        Assert.Equal("alice-1", note.CreatedBy);
        Assert.Equal("Alice Example", note.CreatedByName);
        Assert.Equal(TickedNow, note.CreatedAt);
        Assert.Equal(TickedNow, workItem.LastModifiedAt);
        await _persistence.Received(1).ReplaceAsync(workItem, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddNote_returns_invalid_note_when_text_is_blank()
    {
        var type = BuildType();
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(type).AddNoteAsync(
            workItem.Id, "   ", User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidNote, result.FailureCode);
        Assert.Empty(workItem.Notes);
        await _persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddNote_returns_invalid_note_when_text_exceeds_limit()
    {
        var type = BuildType();
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var oversized = new string('x', WorkItemService.MaxNoteLength + 1);
        var result = await BuildService(type).AddNoteAsync(
            workItem.Id, oversized, User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidNote, result.FailureCode);
        Assert.Empty(workItem.Notes);
    }

    [Fact]
    public async Task AddNote_returns_not_found_when_work_item_missing()
    {
        var type = BuildType();
        _persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var result = await BuildService(type).AddNoteAsync(
            Guid.NewGuid(), "anything", User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task AddNote_allows_any_authenticated_user_without_assign_role()
    {
        // Notes are an audit narrative; any authenticated user (assessor or
        // otherwise) may add one. We assert this explicitly so a future change
        // doesn't accidentally tighten authorization.
        var type = BuildType();
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var standardUser = UserWithRoles("alice-1", "standard");
        var result = await BuildService(type).AddNoteAsync(
            workItem.Id, "Note from a standard user.", standardUser, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Single(workItem.Notes);
    }

    private sealed class FakeTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
