using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

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
        new(new ClaimsIdentity(
        [
            new Claim("cognito:client_id", "test-client"),
            new Claim("user:id", "test-user")
        ], "test"));

    private static ClaimsPrincipal UserWithoutActorId() =>
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
        Assert.True(result.IsIdempotentReplay,
            "Re-completing an already-complete task must be flagged as a replay so " +
            "the endpoint can set X-Idempotent-Replay: true.");
        Assert.Equal(InitialNow, workItem.LastModifiedAt);
        await _persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain(workItem.AuditLog, a => a.Action == "task-completed");
    }

    [Fact]
    public async Task CompleteTask_treats_existing_completion_as_idempotent_after_bson_round_trip_with_different_casing()
    {
        // Regression for epr-aq5: a task id written as "Task1" must be
        // recognised as already-complete when re-completed as "task1" on a
        // freshly-loaded (Mongo round-tripped) work item.
        WorkItemBsonRegistration.Register();

        var type = BuildType(tasksByState: new()
        {
            ["submitted"] = [new WorkItemTask("task1", "Task one")]
        });
        var seed = ExistingWorkItem(completed: new()
        {
            ["submitted"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Task1" }
        });
        var reloaded = BsonSerializer.Deserialize<WorkItem>(seed.ToBsonDocument());
        _persistence.GetByIdAsync(reloaded.Id, Arg.Any<CancellationToken>()).Returns(reloaded);

        var result = await BuildService(type).CompleteTaskAsync(
            reloaded.Id, "task1", User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsIdempotentReplay,
            "Engine must recognise the already-complete task across casing differences " +
            "after a Mongo round-trip.");
        await _persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteTask_treats_existing_completion_as_idempotent_after_bson_round_trip_with_different_state_casing()
    {
        // Regression for epr-aq5: the dictionary key (state id) must also
        // match case-insensitively after a Mongo round-trip. A bucket
        // recorded under "Submitted" must be found under "submitted".
        WorkItemBsonRegistration.Register();

        var type = BuildType(tasksByState: new()
        {
            ["submitted"] = [new WorkItemTask("task1", "Task one")]
        });
        var seed = ExistingWorkItem(stateId: "submitted", completed: new()
        {
            ["Submitted"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "task1" }
        });
        var reloaded = BsonSerializer.Deserialize<WorkItem>(seed.ToBsonDocument());
        _persistence.GetByIdAsync(reloaded.Id, Arg.Any<CancellationToken>()).Returns(reloaded);

        var result = await BuildService(type).CompleteTaskAsync(
            reloaded.Id, "task1", User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsIdempotentReplay);
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
    public async Task ApplyAction_blocks_transition_when_only_canonical_map_marks_task_incomplete()
    {
        // epr-08y: HasIncompleteTasks must consult TaskStatusesByState
        // first (canonical per epr-gl6) and only fall back to the legacy
        // CompletedTaskIdsByState bucket. If a future code path writes
        // only to the canonical map, gating must still respect it.
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
            // Stale legacy bucket says the task IS complete...
            ["submitted"] = ["check-eligibility"]
        });
        // ...but the canonical per-task status map says it is in progress.
        workItem.TaskStatusesByState["submitted"] =
            new(StringComparer.OrdinalIgnoreCase) { ["check-eligibility"] = WorkItemTaskStatus.InProgress };
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(type).ApplyActionAsync(
            workItem.Id, "approve", User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.IncompleteTasks, result.FailureCode);
        Assert.Equal("submitted", workItem.StateId);
        await _persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAction_transitions_when_only_canonical_map_marks_task_complete()
    {
        // epr-08y: a v2 module that writes only to the canonical
        // TaskStatusesByState (without dual-writing the legacy bucket)
        // must still be allowed to transition once tasks are Completed.
        var type = BuildType(
            tasksByState: new()
            {
                ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
            },
            transitions: [
                new WorkItemTransition("approve", "Approve", "submitted", "approved")
            ]);
        var workItem = ExistingWorkItem();
        // Canonical only — legacy CompletedTaskIdsByState bucket left empty.
        workItem.TaskStatusesByState["submitted"] =
            new(StringComparer.OrdinalIgnoreCase) { ["check-eligibility"] = WorkItemTaskStatus.Completed };
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(type).ApplyActionAsync(
            workItem.Id, "approve", User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("approved", workItem.StateId);
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
    public async Task ApplyAction_returns_NotAuthorized_when_caller_lacks_required_role()
    {
        var type = BuildType(transitions: [
            new WorkItemTransition(
                "approve", "Approve", "submitted", "approved",
                RequiredRoles: new[] { "decision-maker" })
        ]);
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(type).ApplyActionAsync(
            workItem.Id, "approve", UserWithRoles("alice-1"), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.NotAuthorized, result.FailureCode);
        Assert.Equal("submitted", workItem.StateId);
        await _persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAction_succeeds_when_caller_holds_one_of_required_roles()
    {
        var type = BuildType(transitions: [
            new WorkItemTransition(
                "approve", "Approve", "submitted", "approved",
                RequiredRoles: new[] { "decision-maker", "admin" })
        ]);
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(type).ApplyActionAsync(
            workItem.Id, "approve", UserWithRoles("bob-1", "admin"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("approved", workItem.StateId);
    }

    [Fact]
    public async Task CompleteTask_returns_ConcurrencyConflict_when_persistence_throws()
    {
        var type = BuildType(tasksByState: new()
        {
            ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
        });
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);
        _persistence
            .When(p => p.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new WorkItemConcurrencyException(workItem.Id, 0));

        var result = await BuildService(type).CompleteTaskAsync(
            workItem.Id, "check-eligibility", User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.FailureCode);
    }

    [Fact]
    public async Task ApplyAction_returns_ConcurrencyConflict_when_persistence_throws()
    {
        var type = BuildType(transitions: [
            new WorkItemTransition("approve", "Approve", "submitted", "approved",
                RequiresAllTasksComplete: false)
        ]);
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);
        _persistence
            .When(p => p.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new WorkItemConcurrencyException(workItem.Id, 0));

        var result = await BuildService(type).ApplyActionAsync(
            workItem.Id, "approve", User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.FailureCode);
    }

    [Fact]
    public async Task CompleteTask_returns_MissingActorIdentity_when_user_id_absent()
    {
        var type = BuildType(tasksByState: new()
        {
            ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
        });
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(type).CompleteTaskAsync(
            workItem.Id, "check-eligibility", UserWithoutActorId(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.MissingActorIdentity, result.FailureCode);
        await _persistence.DidNotReceiveWithAnyArgs()
            .ReplaceAsync(default!, default);
    }

    [Fact]
    public async Task AddNote_records_user_id_verbatim_without_falling_back_to_client_id()
    {
        var type = BuildType();
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(type).AddNoteAsync(
            workItem.Id, "An audit-worthy observation.",
            User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);
        var note = Assert.Single(workItem.Notes);
        Assert.Equal("test-user", note.CreatedBy);
        var auditEntry = Assert.Single(
            workItem.AuditLog, a => a.Action == "note-added");
        Assert.Equal("test-user", auditEntry.CreatedBy);
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
        Assert.True(result.IsIdempotentReplay,
            "Re-assigning to the same user must be flagged as a replay so " +
            "the endpoint can set X-Idempotent-Replay: true.");
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
        Assert.True(result.IsIdempotentReplay,
            "Unassigning an already-unassigned item must be flagged as a replay so " +
            "the endpoint can set X-Idempotent-Replay: true.");
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

    // ---------------------- Audit log (RA-97) ----------------------
    //
    // The framework auto-records every successful state-changing engine call
    // so modules inherit a complete audit trail without writing any audit
    // code themselves. These tests assert that contract.

    private static ClaimsPrincipal AuditUser(string userId = "alice-1", string userName = "Alice Example") =>
        new(new ClaimsIdentity(
        [
            new Claim("cognito:client_id", "test-client"),
            new Claim("user:id", userId),
            new Claim("user:name", userName)
        ], "test"));

    [Fact]
    public async Task Audit_CompleteTask_appends_entry_with_actor_and_task_details()
    {
        var type = BuildType(tasksByState: new()
        {
            ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
        });
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        await BuildService(type).CompleteTaskAsync(
            workItem.Id, "check-eligibility", AuditUser(), TestContext.Current.CancellationToken);

        var entry = Assert.Single(workItem.AuditLog);
        Assert.Equal("task-completed", entry.Action);
        Assert.Equal("Task completed", entry.ActionDisplayName);
        Assert.Equal("alice-1", entry.CreatedBy);
        Assert.Equal("Alice Example", entry.CreatedByName);
        Assert.Equal(TickedNow, entry.CreatedAt);
        Assert.Equal("check-eligibility", entry.Details["taskId"]);
        Assert.Equal("Check eligibility", entry.Details["taskDisplayName"]);
        Assert.Equal("submitted", entry.Details["stateId"]);
    }

    [Fact]
    public async Task Audit_CompleteTask_idempotent_call_does_not_append_a_second_entry()
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

        await BuildService(type).CompleteTaskAsync(
            workItem.Id, "check-eligibility", AuditUser(), TestContext.Current.CancellationToken);

        Assert.Empty(workItem.AuditLog);
    }

    [Fact]
    public async Task Audit_CompleteTask_failure_does_not_append_an_entry()
    {
        var type = BuildType(tasksByState: new()
        {
            ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
        });
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(type).CompleteTaskAsync(
            workItem.Id, "unknown-task", AuditUser(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Empty(workItem.AuditLog);
    }

    [Fact]
    public async Task Audit_ApplyAction_records_from_and_to_state()
    {
        var type = BuildType(transitions: [
            new WorkItemTransition("withdraw", "Withdraw", "submitted", "rejected", RequiresAllTasksComplete: false)
        ]);
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        await BuildService(type).ApplyActionAsync(
            workItem.Id, "withdraw", AuditUser(), TestContext.Current.CancellationToken);

        var entry = Assert.Single(workItem.AuditLog);
        Assert.Equal("action-applied", entry.Action);
        Assert.Equal("Action applied", entry.ActionDisplayName);
        Assert.Equal("withdraw", entry.Details["actionId"]);
        Assert.Equal("Withdraw", entry.Details["actionDisplayName"]);
        Assert.Equal("submitted", entry.Details["fromStateId"]);
        Assert.Equal("rejected", entry.Details["toStateId"]);
        Assert.Equal("alice-1", entry.CreatedBy);
        Assert.Equal(TickedNow, entry.CreatedAt);
    }

    [Fact]
    public async Task Audit_ApplyAction_invalid_transition_does_not_append_an_entry()
    {
        var type = BuildType(
            tasksByState: new()
            {
                ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
            },
            transitions: [
                new WorkItemTransition("approve", "Approve", "submitted", "approved")
            ]);
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(type).ApplyActionAsync(
            workItem.Id, "approve", AuditUser(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Empty(workItem.AuditLog);
    }

    [Fact]
    public async Task Audit_Assign_records_assignee_and_previous_assignee()
    {
        var type = BuildType();
        var workItem = ExistingWorkItem();
        workItem.AssignedToId = "bob-1";
        workItem.AssignedToName = "Bob Example";
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var actor = UserWithRoles("alice-1", WorkItemService.AssignRole);
        await BuildService(type).AssignAsync(
            workItem.Id, "carol-1", "Carol Example", actor, TestContext.Current.CancellationToken);

        var entry = Assert.Single(workItem.AuditLog);
        Assert.Equal("assigned", entry.Action);
        Assert.Equal("Assigned", entry.ActionDisplayName);
        Assert.Equal("carol-1", entry.Details["assigneeId"]);
        Assert.Equal("Carol Example", entry.Details["assigneeName"]);
        Assert.Equal("bob-1", entry.Details["previousAssigneeId"]);
        Assert.Equal("Bob Example", entry.Details["previousAssigneeName"]);
        Assert.Equal("alice-1", entry.CreatedBy);
    }

    [Fact]
    public async Task Audit_Assign_idempotent_call_does_not_append_an_entry()
    {
        var type = BuildType();
        var workItem = ExistingWorkItem();
        workItem.AssignedToId = "alice-1";
        workItem.AssignedToName = "Alice";
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var actor = UserWithRoles("actor-1", WorkItemService.AssignRole);
        await BuildService(type).AssignAsync(
            workItem.Id, "alice-1", "Alice", actor, TestContext.Current.CancellationToken);

        Assert.Empty(workItem.AuditLog);
    }

    [Fact]
    public async Task Audit_Assign_authorization_failure_does_not_append_an_entry()
    {
        var type = BuildType();
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var actor = UserWithRoles("alice-1", "standard");
        var result = await BuildService(type).AssignAsync(
            workItem.Id, "bob-1", "Bob", actor, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Empty(workItem.AuditLog);
    }

    [Fact]
    public async Task Audit_Unassign_records_previous_assignee()
    {
        var type = BuildType();
        var workItem = ExistingWorkItem();
        workItem.AssignedToId = "alice-1";
        workItem.AssignedToName = "Alice";
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var actor = UserWithRoles("actor-1", WorkItemService.AssignRole);
        await BuildService(type).UnassignAsync(workItem.Id, actor, TestContext.Current.CancellationToken);

        var entry = Assert.Single(workItem.AuditLog);
        Assert.Equal("unassigned", entry.Action);
        Assert.Equal("Unassigned", entry.ActionDisplayName);
        Assert.Equal("alice-1", entry.Details["previousAssigneeId"]);
        Assert.Equal("Alice", entry.Details["previousAssigneeName"]);
        Assert.Equal("actor-1", entry.CreatedBy);
    }

    [Fact]
    public async Task Audit_Unassign_already_unassigned_does_not_append_an_entry()
    {
        var type = BuildType();
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var actor = UserWithRoles("actor-1", WorkItemService.AssignRole);
        await BuildService(type).UnassignAsync(workItem.Id, actor, TestContext.Current.CancellationToken);

        Assert.Empty(workItem.AuditLog);
    }

    [Fact]
    public async Task Audit_AddNote_records_note_id()
    {
        var type = BuildType();
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        await BuildService(type).AddNoteAsync(
            workItem.Id, "  A note.  ", AuditUser(), TestContext.Current.CancellationToken);

        var note = Assert.Single(workItem.Notes);
        var entry = Assert.Single(workItem.AuditLog);
        Assert.Equal("note-added", entry.Action);
        Assert.Equal("Note added", entry.ActionDisplayName);
        Assert.Equal(note.Id.ToString(), entry.Details["noteId"]);
        // epr-27o: the audit entry snapshots the trimmed note body so a
        // reader of the audit log can see what was written without
        // cross-referencing the Notes collection by id.
        Assert.Equal("A note.", entry.Details["noteText"]);
        Assert.Equal(note.Text, entry.Details["noteText"]);
        Assert.Equal("alice-1", entry.CreatedBy);
        Assert.Equal("Alice Example", entry.CreatedByName);
        Assert.Equal(TickedNow, entry.CreatedAt);
    }

    [Fact]
    public async Task Audit_AddNote_validation_failure_does_not_append_an_entry()
    {
        var type = BuildType();
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(type).AddNoteAsync(
            workItem.Id, "   ", AuditUser(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Empty(workItem.AuditLog);
    }

    [Fact]
    public async Task Audit_log_is_chronological_across_a_sequence_of_actions()
    {
        var type = BuildType(
            tasksByState: new()
            {
                ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
            },
            transitions: [
                new WorkItemTransition("approve", "Approve", "submitted", "approved")
            ]);
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var time = new MutableTimeProvider(TickedNow);
        var service = new WorkItemService(
            new WorkItemRegistry([type]),
            _persistence,
            NullLogger<WorkItemService>.Instance,
            time);

        await service.AddNoteAsync(workItem.Id, "first", AuditUser(), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(1));
        await service.CompleteTaskAsync(workItem.Id, "check-eligibility", AuditUser(), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(1));
        await service.ApplyActionAsync(workItem.Id, "approve", AuditUser(), TestContext.Current.CancellationToken);

        Assert.Equal(3, workItem.AuditLog.Count);
        Assert.Equal(["note-added", "task-completed", "action-applied"],
            workItem.AuditLog.Select(e => e.Action).ToArray());
        // Strictly increasing timestamps — entries are appended in
        // chronological (insertion) order on disk.
        Assert.True(workItem.AuditLog[0].CreatedAt < workItem.AuditLog[1].CreatedAt);
        Assert.True(workItem.AuditLog[1].CreatedAt < workItem.AuditLog[2].CreatedAt);
    }

    private sealed class MutableTimeProvider(DateTime initial) : TimeProvider
    {
        private DateTime _now = initial;
        public override DateTimeOffset GetUtcNow() => new(_now, TimeSpan.Zero);
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    // ---------------------- SubmitAsync (RA-97 birth event) ----------------------
    //
    // The audit timeline must start at the work item's submission rather
    // than at the first task completion. The framework writes a single
    // 'work-item-submitted' entry inside the same CreateAsync call that
    // persists the new document.

    [Fact]
    public async Task Submit_persists_work_item_with_initial_state_and_template_snapshot()
    {
        var type = BuildType();
        var payload = new BsonDocument { ["foo"] = "bar" };

        WorkItem? captured = null;
        await _persistence.CreateAsync(Arg.Do<WorkItem>(w => captured = w), Arg.Any<CancellationToken>());

        var result = await BuildService(type).SubmitAsync(
            type, payload, "test-client", AuditUser(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(captured);
        Assert.Equal(TypeId, captured!.TypeId);
        Assert.Equal("submitted", captured.StateId);
        Assert.Equal("test-client", captured.SubmittedBy);
        Assert.Equal(TickedNow, captured.SubmittedAt);
        Assert.Equal(TickedNow, captured.LastModifiedAt);
        Assert.NotNull(captured.TemplateSnapshot);
        Assert.Equal("v1", captured.TemplateVersion);
        Assert.Equal("v1", captured.TemplateSnapshot!.TemplateVersion);
        Assert.Equal("bar", captured.Payload["foo"].AsString);
        await _persistence.Received(1).CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Submit_appends_single_work_item_submitted_audit_entry_in_same_create_call()
    {
        var type = BuildType();

        // Snapshot the AuditLog at the moment CreateAsync is invoked so we
        // can prove the submission entry is part of the same write — not
        // appended after the fact.
        List<WorkItemAuditEntry>? auditAtCreate = null;
        await _persistence.CreateAsync(
            Arg.Do<WorkItem>(w => auditAtCreate = w.AuditLog.ToList()),
            Arg.Any<CancellationToken>());

        var result = await BuildService(type).SubmitAsync(
            type, new BsonDocument(), "test-client", AuditUser(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(auditAtCreate);
        var entry = Assert.Single(auditAtCreate!);
        Assert.Equal("work-item-submitted", entry.Action);
        Assert.Equal("Work item submitted", entry.ActionDisplayName);
        Assert.Equal("alice-1", entry.CreatedBy);
        Assert.Equal("Alice Example", entry.CreatedByName);
        // The audit entry's timestamp matches WorkItem.SubmittedAt — both
        // come from the injected TimeProvider in the same call.
        Assert.Equal(result.WorkItem!.SubmittedAt, entry.CreatedAt);
        Assert.Equal(TickedNow, entry.CreatedAt);
        Assert.Equal(TypeId, entry.Details["typeId"]);
        Assert.Equal("submitted", entry.Details["stateId"]);
        Assert.Equal("v1", entry.Details["templateVersion"]);
    }

    [Fact]
    public async Task Submit_returns_missing_actor_identity_and_persists_nothing_when_user_id_claim_absent()
    {
        var type = BuildType();

        var result = await BuildService(type).SubmitAsync(
            type, new BsonDocument(), "test-client",
            UserWithoutActorId(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.MissingActorIdentity, result.FailureCode);
        await _persistence.DidNotReceive().CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }
}