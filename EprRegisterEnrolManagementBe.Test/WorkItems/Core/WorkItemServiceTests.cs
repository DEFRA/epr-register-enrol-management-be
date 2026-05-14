using System.Security.Claims;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// epr-efp: backed by ephemeral MongoDB. Persistence is the real
/// <see cref="WorkItemPersistence"/>; assertions are made against the
/// document fetched back from Mongo, not against the in-memory instance
/// the test author handed to the engine.
/// </summary>
public class WorkItemServiceTests
    : IClassFixture<MongoIntegrationFixture>, IAsyncDisposable
{
    private const string TypeId = "test-type";
    private static readonly DateTime InitialNow = new(2026, 4, 27, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TickedNow = InitialNow.AddMinutes(5);

    private readonly TestMongoDbClientFactory _clientFactory;
    private readonly string _databaseName;
    private readonly WorkItemPersistence _persistence;
    private readonly FakeTimeProvider _time = new(TickedNow);

    public WorkItemServiceTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("svc");
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new WorkItemPersistence(_clientFactory, NullLoggerFactory.Instance);
    }

    public async ValueTask DisposeAsync() =>
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

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

    private async Task<WorkItem> SeedAsync(
        string stateId = "submitted",
        Dictionary<string, HashSet<string>>? completed = null,
        Action<WorkItem>? configure = null)
    {
        var workItem = new WorkItem
        {
            Id = Guid.NewGuid(),
            TypeId = TypeId,
            StateId = stateId,
            SubmittedAt = InitialNow,
            LastModifiedAt = InitialNow,
            SubmittedBy = "test-client"
        };
        if (completed is not null)
        {
            foreach (var (state, tasks) in completed)
            {
                workItem.CompletedTaskIdsByState[state] =
                    new HashSet<string>(tasks, StringComparer.OrdinalIgnoreCase);
            }
        }
        configure?.Invoke(workItem);
        await _persistence.CreateAsync(workItem, TestContext.Current.CancellationToken);
        return workItem;
    }

    private async Task<WorkItem> GetAsync(Guid id)
    {
        var fetched = await _persistence.GetByIdAsync(id, TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        return fetched!;
    }

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
        var workItem = await SeedAsync();

        var result = await BuildService(type).CompleteTaskAsync(
            workItem.Id, "check-eligibility", User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var fetched = await GetAsync(workItem.Id);
        Assert.Contains("check-eligibility", fetched.CompletedTaskIdsByState["submitted"]);
        Assert.Equal(TickedNow, fetched.LastModifiedAt);
        Assert.Equal(1, fetched.Version);
    }

    [Fact]
    public async Task CompleteTask_is_idempotent_when_already_complete()
    {
        var type = BuildType(tasksByState: new()
        {
            ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
        });
        var workItem = await SeedAsync(completed: new()
        {
            ["submitted"] = ["check-eligibility"]
        });

        var result = await BuildService(type).CompleteTaskAsync(
            workItem.Id, "check-eligibility", User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsIdempotentReplay,
            "Re-completing an already-complete task must be flagged as a replay so " +
            "the endpoint can set X-Idempotent-Replay: true.");

        var fetched = await GetAsync(workItem.Id);
        Assert.Equal(InitialNow, fetched.LastModifiedAt);
        Assert.Equal(0, fetched.Version);
        Assert.DoesNotContain(fetched.AuditLog, a => a.Action == "task-completed");
    }

    [Fact]
    public async Task CompleteTask_treats_existing_completion_as_idempotent_after_bson_round_trip_with_different_casing()
    {
        // Regression for epr-aq5: a task id written as "Task1" must be
        // recognised as already-complete when re-completed as "task1" on a
        // freshly-loaded (Mongo round-tripped) work item. The ephemeral
        // MongoDB load IS the round-trip — no manual ToBsonDocument needed.
        WorkItemBsonRegistration.Register();

        var type = BuildType(tasksByState: new()
        {
            ["submitted"] = [new WorkItemTask("task1", "Task one")]
        });
        var workItem = await SeedAsync(completed: new()
        {
            ["submitted"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Task1" }
        });

        var result = await BuildService(type).CompleteTaskAsync(
            workItem.Id, "task1", User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsIdempotentReplay,
            "Engine must recognise the already-complete task across casing differences " +
            "after a Mongo round-trip.");
        var fetched = await GetAsync(workItem.Id);
        Assert.Equal(0, fetched.Version);
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
        var workItem = await SeedAsync(stateId: "submitted", completed: new()
        {
            ["Submitted"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "task1" }
        });

        var result = await BuildService(type).CompleteTaskAsync(
            workItem.Id, "task1", User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsIdempotentReplay);
        var fetched = await GetAsync(workItem.Id);
        Assert.Equal(0, fetched.Version);
    }

    [Fact]
    public async Task CompleteTask_fails_when_task_does_not_apply_to_current_state()
    {
        var type = BuildType(tasksByState: new()
        {
            ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
        });
        var workItem = await SeedAsync();

        var result = await BuildService(type).CompleteTaskAsync(
            workItem.Id, "unknown-task", User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.TaskNotApplicable, result.FailureCode);
        var fetched = await GetAsync(workItem.Id);
        Assert.Equal(0, fetched.Version);
    }

    [Fact]
    public async Task CompleteTask_returns_not_found_when_work_item_missing()
    {
        var type = BuildType();

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
        var workItem = await SeedAsync(completed: new()
        {
            ["submitted"] = ["check-eligibility"]
        });

        var result = await BuildService(type).ApplyActionAsync(
            workItem.Id, "approve", User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.IncompleteTasks, result.FailureCode);
        var fetched = await GetAsync(workItem.Id);
        Assert.Equal("submitted", fetched.StateId);
        Assert.Equal(0, fetched.Version);
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
        var workItem = await SeedAsync(completed: new()
        {
            ["submitted"] = ["check-eligibility"]
        });

        var result = await BuildService(type).ApplyActionAsync(
            workItem.Id, "approve", User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var fetched = await GetAsync(workItem.Id);
        Assert.Equal("approved", fetched.StateId);
        Assert.Equal(TickedNow, fetched.LastModifiedAt);
        Assert.Equal(1, fetched.Version);
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
        var workItem = await SeedAsync(
            completed: new()
            {
                // Stale legacy bucket says the task IS complete...
                ["submitted"] = ["check-eligibility"]
            },
            configure: w =>
            {
                // ...but the canonical per-task status map says it is in progress.
                w.TaskStatusesByState["submitted"] =
                    new(StringComparer.OrdinalIgnoreCase) { ["check-eligibility"] = WorkItemTaskStatus.InProgress };
            });

        var result = await BuildService(type).ApplyActionAsync(
            workItem.Id, "approve", User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.IncompleteTasks, result.FailureCode);
        var fetched = await GetAsync(workItem.Id);
        Assert.Equal("submitted", fetched.StateId);
        Assert.Equal(0, fetched.Version);
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
        var workItem = await SeedAsync(configure: w =>
        {
            // Canonical only — legacy CompletedTaskIdsByState bucket left empty.
            w.TaskStatusesByState["submitted"] =
                new(StringComparer.OrdinalIgnoreCase) { ["check-eligibility"] = WorkItemTaskStatus.Completed };
        });

        var result = await BuildService(type).ApplyActionAsync(
            workItem.Id, "approve", User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var fetched = await GetAsync(workItem.Id);
        Assert.Equal("approved", fetched.StateId);
        Assert.Equal(1, fetched.Version);
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
        var workItem = await SeedAsync();

        var result = await BuildService(type).ApplyActionAsync(
            workItem.Id, "withdraw", User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var fetched = await GetAsync(workItem.Id);
        Assert.Equal("rejected", fetched.StateId);
    }

    [Fact]
    public async Task ApplyAction_fails_when_action_does_not_apply_to_current_state()
    {
        var type = BuildType(transitions: [
            new WorkItemTransition("approve", "Approve", "submitted", "approved")
        ]);
        var workItem = await SeedAsync(stateId: "approved");

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
        var workItem = await SeedAsync();

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
        var workItem = await SeedAsync();

        var result = await BuildService(type).ApplyActionAsync(
            workItem.Id, "approve", UserWithRoles("alice-1"), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.NotAuthorized, result.FailureCode);
        var fetched = await GetAsync(workItem.Id);
        Assert.Equal("submitted", fetched.StateId);
        Assert.Equal(0, fetched.Version);
    }

    [Fact]
    public async Task ApplyAction_succeeds_when_caller_holds_one_of_required_roles()
    {
        var type = BuildType(transitions: [
            new WorkItemTransition(
                "approve", "Approve", "submitted", "approved",
                RequiredRoles: new[] { "decision-maker", "admin" })
        ]);
        var workItem = await SeedAsync();

        var result = await BuildService(type).ApplyActionAsync(
            workItem.Id, "approve", UserWithRoles("bob-1", "admin"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var fetched = await GetAsync(workItem.Id);
        Assert.Equal("approved", fetched.StateId);
    }

    [Fact]
    public async Task CompleteTask_returns_ConcurrencyConflict_when_persistence_throws()
    {
        // Real concurrency conflict via on-disk version race rather than
        // a mocked exception (epr-efp).
        var type = BuildType(tasksByState: new()
        {
            ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
        });
        var workItem = await SeedAsync();
        var racingService = BuildRacingService(type, workItem.Id);

        var result = await racingService.CompleteTaskAsync(
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
        var workItem = await SeedAsync();
        var racingService = BuildRacingService(type, workItem.Id);

        var result = await racingService.ApplyActionAsync(
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
        var workItem = await SeedAsync();

        var result = await BuildService(type).CompleteTaskAsync(
            workItem.Id, "check-eligibility", UserWithoutActorId(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.MissingActorIdentity, result.FailureCode);
        var fetched = await GetAsync(workItem.Id);
        Assert.Equal(0, fetched.Version);
    }

    [Fact]
    public async Task AddNote_records_user_id_verbatim_without_falling_back_to_client_id()
    {
        var type = BuildType();
        var workItem = await SeedAsync();

        var result = await BuildService(type).AddNoteAsync(
            workItem.Id, "An audit-worthy observation.",
            User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);
        var fetched = await GetAsync(workItem.Id);
        var note = Assert.Single(fetched.Notes);
        Assert.Equal("test-user", note.CreatedBy);
        var auditEntry = Assert.Single(
            fetched.AuditLog, a => a.Action == "note-added");
        Assert.Equal("test-user", auditEntry.CreatedBy);
    }

    [Fact]
    public async Task Project_lists_only_actions_whose_preconditions_are_met()
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
        // Project is a pure read-only function over an in-memory document
        // (no persistence call), so a hand-built instance exercises the
        // same code path.
        var workItem = new WorkItem
        {
            Id = Guid.NewGuid(),
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedAt = InitialNow,
            LastModifiedAt = InitialNow,
            SubmittedBy = "test-client"
        };

        var projection = BuildService(type).Project(workItem);

        Assert.Single(projection.Tasks);
        Assert.False(projection.Tasks.Single().IsComplete);

        // Approve and reject are gated; withdraw is always available.
        Assert.Equal(["withdraw"], projection.AvailableActions.Select(a => a.ActionId).ToArray());
    }

    [Fact]
    public async Task Project_returns_no_actions_for_terminal_state()
    {
        var type = BuildType(transitions: [
            new WorkItemTransition("approve", "Approve", "submitted", "approved")
        ]);
        var workItem = new WorkItem
        {
            Id = Guid.NewGuid(),
            TypeId = TypeId,
            StateId = "approved",
            SubmittedAt = InitialNow,
            LastModifiedAt = InitialNow,
            SubmittedBy = "test-client"
        };

        var projection = BuildService(type).Project(workItem);

        Assert.Empty(projection.AvailableActions);
        Assert.Empty(projection.Tasks);
        await Task.CompletedTask;
    }

    // ---------------------- Assignment ----------------------

    [Fact]
    public async Task Assign_records_assignee_with_snapshot_and_audit_metadata()
    {
        var type = BuildType();
        var workItem = await SeedAsync();

        var actor = UserWithRoles("actor-1", WorkItemService.AssignRole);
        var result = await BuildService(type).AssignAsync(
            workItem.Id, "alice-1", "Alice Example", actor, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var fetched = await GetAsync(workItem.Id);
        Assert.Equal("alice-1", fetched.AssignedToId);
        Assert.Equal("Alice Example", fetched.AssignedToName);
        Assert.Equal(TickedNow, fetched.AssignedAt);
        Assert.Equal("actor-1", fetched.AssignedBy);
        Assert.Equal(TickedNow, fetched.LastModifiedAt);
        Assert.Equal(1, fetched.Version);
    }

    [Fact]
    public async Task Assign_re_assignment_replaces_previous_assignee()
    {
        var type = BuildType();
        var workItem = await SeedAsync(configure: w =>
        {
            w.AssignedToId = "bob-1";
            w.AssignedToName = "Bob";
            w.AssignedAt = InitialNow;
            w.AssignedBy = "old-actor";
        });

        var actor = UserWithRoles("actor-1", WorkItemService.AssignRole);
        var result = await BuildService(type).AssignAsync(
            workItem.Id, "carol-1", "Carol", actor, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var fetched = await GetAsync(workItem.Id);
        Assert.Equal("carol-1", fetched.AssignedToId);
        Assert.Equal("Carol", fetched.AssignedToName);
        Assert.Equal("actor-1", fetched.AssignedBy);
    }

    [Fact]
    public async Task Assign_is_idempotent_when_assignee_unchanged()
    {
        var type = BuildType();
        var workItem = await SeedAsync(configure: w =>
        {
            w.AssignedToId = "alice-1";
            w.AssignedToName = "Alice";
            w.AssignedAt = InitialNow;
            w.AssignedBy = "old-actor";
        });

        var actor = UserWithRoles("actor-1", WorkItemService.AssignRole);
        var result = await BuildService(type).AssignAsync(
            workItem.Id, "alice-1", "Alice", actor, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsIdempotentReplay,
            "Re-assigning to the same user must be flagged as a replay so " +
            "the endpoint can set X-Idempotent-Replay: true.");
        var fetched = await GetAsync(workItem.Id);
        Assert.Equal(InitialNow, fetched.AssignedAt);
        Assert.Equal("old-actor", fetched.AssignedBy);
        Assert.Equal(0, fetched.Version);
    }

    [Fact]
    public async Task Assign_blank_assignee_id_is_rejected()
    {
        var type = BuildType();
        var workItem = await SeedAsync();

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
        var workItem = await SeedAsync();

        var actor = UserWithRoles("alice-1", "standard");
        var result = await BuildService(type).AssignAsync(
            workItem.Id, "alice-1", "Alice", actor, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var fetched = await GetAsync(workItem.Id);
        Assert.Equal("alice-1", fetched.AssignedToId);
    }

    [Fact]
    public async Task Assign_actor_with_no_role_is_forbidden(/* epr-6e5 */)
    {
        // The "assign" / "standard" cases are covered above; pin the
        // third branch — an actor with no recognised role at all —
        // explicitly. The current contract (per AGENTS.md and
        // WorkItemService.AssignAsync) is "anyone without the assign
        // role is treated as a standard user", which means a no-role
        // actor can self-assign an unassigned item but cannot do
        // anything else. This test pins the cannot-do-anything-else
        // half of that contract.
        var type = BuildType();
        var workItem = await SeedAsync(configure: w =>
        {
            w.AssignedToId = "bob-1";
            w.AssignedToName = "Bob";
        });

        // Deliberately pass no roles. The work item is already
        // assigned to bob-1; alice-1 has no permission to take it.
        var actor = UserWithRoles("alice-1");
        var result = await BuildService(type).AssignAsync(
            workItem.Id, "alice-1", "Alice", actor, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.NotAuthorized, result.FailureCode);
        var fetched = await GetAsync(workItem.Id);
        Assert.Equal("bob-1", fetched.AssignedToId);
        Assert.Equal(0, fetched.Version);
    }

    [Fact]
    public async Task Assign_standard_user_cannot_assign_to_someone_else()
    {
        var type = BuildType();
        var workItem = await SeedAsync();

        var actor = UserWithRoles("alice-1", "standard");
        var result = await BuildService(type).AssignAsync(
            workItem.Id, "bob-1", "Bob", actor, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.NotAuthorized, result.FailureCode);
        var fetched = await GetAsync(workItem.Id);
        Assert.Equal(0, fetched.Version);
    }

    [Fact]
    public async Task Assign_standard_user_cannot_take_item_already_assigned_to_another_user()
    {
        var type = BuildType();
        var workItem = await SeedAsync(configure: w =>
        {
            w.AssignedToId = "bob-1";
            w.AssignedToName = "Bob";
        });

        var actor = UserWithRoles("alice-1", "standard");
        var result = await BuildService(type).AssignAsync(
            workItem.Id, "alice-1", "Alice", actor, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.NotAuthorized, result.FailureCode);
        var fetched = await GetAsync(workItem.Id);
        Assert.Equal("bob-1", fetched.AssignedToId);
    }

    [Fact]
    public async Task Unassign_clears_assignment_when_actor_has_assign_role()
    {
        var type = BuildType();
        var workItem = await SeedAsync(configure: w =>
        {
            w.AssignedToId = "alice-1";
            w.AssignedToName = "Alice";
            w.AssignedAt = InitialNow;
            w.AssignedBy = "actor-1";
        });

        var actor = UserWithRoles("actor-2", WorkItemService.AssignRole);
        var result = await BuildService(type).UnassignAsync(workItem.Id, actor, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var fetched = await GetAsync(workItem.Id);
        Assert.Null(fetched.AssignedToId);
        Assert.Null(fetched.AssignedToName);
        Assert.Null(fetched.AssignedAt);
        Assert.Null(fetched.AssignedBy);
        Assert.Equal(TickedNow, fetched.LastModifiedAt);
        Assert.Equal(1, fetched.Version);
    }

    [Fact]
    public async Task Unassign_is_idempotent_for_already_unassigned_item()
    {
        var type = BuildType();
        var workItem = await SeedAsync();

        var actor = UserWithRoles("actor-1", WorkItemService.AssignRole);
        var result = await BuildService(type).UnassignAsync(workItem.Id, actor, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsIdempotentReplay,
            "Unassigning an already-unassigned item must be flagged as a replay so " +
            "the endpoint can set X-Idempotent-Replay: true.");
        var fetched = await GetAsync(workItem.Id);
        Assert.Equal(0, fetched.Version);
    }

    [Fact]
    public async Task Unassign_rejected_for_standard_user()
    {
        var type = BuildType();
        var workItem = await SeedAsync(configure: w =>
        {
            w.AssignedToId = "alice-1";
        });

        var actor = UserWithRoles("alice-1", "standard");
        var result = await BuildService(type).UnassignAsync(workItem.Id, actor, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.NotAuthorized, result.FailureCode);
        var fetched = await GetAsync(workItem.Id);
        Assert.Equal("alice-1", fetched.AssignedToId);
        Assert.Equal(0, fetched.Version);
    }

    [Fact]
    public async Task AddNote_appends_note_with_author_snapshot_and_persists()
    {
        var type = BuildType();
        var workItem = await SeedAsync();

        var actor = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("cognito:client_id", "test-client"),
            new Claim("user:id", "alice-1"),
            new Claim("user:name", "Alice Example")
        ], "test"));

        var result = await BuildService(type).AddNoteAsync(
            workItem.Id, "  Spoke to applicant; awaiting evidence.  ", actor, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var fetched = await GetAsync(workItem.Id);
        var note = Assert.Single(fetched.Notes);
        Assert.Equal("Spoke to applicant; awaiting evidence.", note.Text);
        Assert.Equal("alice-1", note.CreatedBy);
        Assert.Equal("Alice Example", note.CreatedByName);
        Assert.Equal(TickedNow, note.CreatedAt);
        Assert.Equal(TickedNow, fetched.LastModifiedAt);
        Assert.Equal(1, fetched.Version);
    }

    [Fact]
    public async Task AddNote_returns_invalid_note_when_text_is_blank()
    {
        var type = BuildType();
        var workItem = await SeedAsync();

        var result = await BuildService(type).AddNoteAsync(
            workItem.Id, "   ", User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidNote, result.FailureCode);
        var fetched = await GetAsync(workItem.Id);
        Assert.Empty(fetched.Notes);
        Assert.Equal(0, fetched.Version);
    }

    [Fact]
    public async Task AddNote_returns_invalid_note_when_text_exceeds_limit()
    {
        var type = BuildType();
        var workItem = await SeedAsync();

        var oversized = new string('x', WorkItemService.MaxNoteLength + 1);
        var result = await BuildService(type).AddNoteAsync(
            workItem.Id, oversized, User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidNote, result.FailureCode);
        var fetched = await GetAsync(workItem.Id);
        Assert.Empty(fetched.Notes);
    }

    [Fact]
    public async Task AddNote_returns_not_found_when_work_item_missing()
    {
        var type = BuildType();

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
        var workItem = await SeedAsync();

        var standardUser = UserWithRoles("alice-1", "standard");
        var result = await BuildService(type).AddNoteAsync(
            workItem.Id, "Note from a standard user.", standardUser, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var fetched = await GetAsync(workItem.Id);
        Assert.Single(fetched.Notes);
    }

    // ---------------------- Task-scoped notes (RA-129 / epr-cky) ----------------------

    [Fact]
    public async Task AddNote_task_scoped_persists_taskId_and_writes_task_note_added_audit_entry()
    {
        var type = BuildType(tasksByState: new()
        {
            ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
        });
        var workItem = await SeedAsync();

        var result = await BuildService(type).AddTaskNoteAsync(
            workItem.Id, "check-eligibility", "Spoke to applicant.", AuditUser(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);
        var fetched = await GetAsync(workItem.Id);
        var note = Assert.Single(fetched.Notes);
        Assert.Equal("check-eligibility", note.TaskId);
        Assert.Equal("Spoke to applicant.", note.Text);
        Assert.Equal("alice-1", note.CreatedBy);

        var entry = Assert.Single(fetched.AuditLog);
        Assert.Equal("task-note-added", entry.Action);
        Assert.Equal("Task note added", entry.ActionDisplayName);
        Assert.Equal("check-eligibility", entry.Details["taskId"]);
        Assert.Equal("Check eligibility", entry.Details["taskDisplayName"]);
        Assert.Equal(note.Id.ToString(), entry.Details["noteId"]);
        // Short note: excerpt mirrors the full trimmed body.
        Assert.Equal("Spoke to applicant.", entry.Details["excerpt"]);
    }

    [Fact]
    public async Task AddNote_task_scoped_excerpt_is_full_text_when_under_or_at_100_chars()
    {
        var type = BuildType(tasksByState: new()
        {
            ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
        });
        var workItem = await SeedAsync();

        var exactlyHundred = new string('x', WorkItemService.TaskNoteAuditExcerptLength);
        var result = await BuildService(type).AddTaskNoteAsync(
            workItem.Id, "check-eligibility", exactlyHundred, AuditUser(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var fetched = await GetAsync(workItem.Id);
        var entry = Assert.Single(fetched.AuditLog);
        Assert.Equal(exactlyHundred, entry.Details["excerpt"]);
        Assert.Equal(WorkItemService.TaskNoteAuditExcerptLength, ((string)entry.Details["excerpt"]!).Length);
    }

    [Fact]
    public async Task AddNote_task_scoped_excerpt_truncates_at_100_chars_for_longer_text()
    {
        var type = BuildType(tasksByState: new()
        {
            ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
        });
        var workItem = await SeedAsync();

        var longBody = new string('a', WorkItemService.TaskNoteAuditExcerptLength)
                       + new string('b', 50);
        var result = await BuildService(type).AddTaskNoteAsync(
            workItem.Id, "check-eligibility", longBody, AuditUser(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var fetched = await GetAsync(workItem.Id);
        var note = Assert.Single(fetched.Notes);
        // Full body persisted; excerpt is just the first N chars.
        Assert.Equal(longBody, note.Text);
        var entry = Assert.Single(fetched.AuditLog);
        var excerpt = (string)entry.Details["excerpt"]!;
        Assert.Equal(WorkItemService.TaskNoteAuditExcerptLength, excerpt.Length);
        Assert.Equal(new string('a', WorkItemService.TaskNoteAuditExcerptLength), excerpt);
    }

    [Fact]
    public async Task AddNote_task_scoped_returns_task_not_applicable_for_unknown_task()
    {
        var type = BuildType(tasksByState: new()
        {
            ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
        });
        var workItem = await SeedAsync();

        var result = await BuildService(type).AddTaskNoteAsync(
            workItem.Id, "no-such-task", "anything", AuditUser(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.TaskNotApplicable, result.FailureCode);
        var fetched = await GetAsync(workItem.Id);
        Assert.Empty(fetched.Notes);
        Assert.Empty(fetched.AuditLog);
        Assert.Equal(0, fetched.Version);
    }

    [Fact]
    public async Task AddNote_task_scoped_returns_not_found_when_work_item_missing()
    {
        var type = BuildType(tasksByState: new()
        {
            ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
        });

        var result = await BuildService(type).AddTaskNoteAsync(
            Guid.NewGuid(), "check-eligibility", "anything", AuditUser(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task AddNote_work_item_level_path_unchanged_when_taskId_omitted()
    {
        // Regression: passing no taskId must keep the historical
        // 'note-added' audit action and persist TaskId = null.
        var type = BuildType();
        var workItem = await SeedAsync();

        var result = await BuildService(type).AddNoteAsync(
            workItem.Id, "Generic note.", AuditUser(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var fetched = await GetAsync(workItem.Id);
        var note = Assert.Single(fetched.Notes);
        Assert.Null(note.TaskId);
        var entry = Assert.Single(fetched.AuditLog);
        Assert.Equal("note-added", entry.Action);
        Assert.False(entry.Details.ContainsKey("excerpt"));
    }

    private sealed class FakeTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    // ---------------------- Audit log (RA-97) ----------------------

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
        var workItem = await SeedAsync();

        await BuildService(type).CompleteTaskAsync(
            workItem.Id, "check-eligibility", AuditUser(), TestContext.Current.CancellationToken);

        var fetched = await GetAsync(workItem.Id);
        var entry = Assert.Single(fetched.AuditLog);
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
        var workItem = await SeedAsync(completed: new()
        {
            ["submitted"] = ["check-eligibility"]
        });

        await BuildService(type).CompleteTaskAsync(
            workItem.Id, "check-eligibility", AuditUser(), TestContext.Current.CancellationToken);

        var fetched = await GetAsync(workItem.Id);
        Assert.Empty(fetched.AuditLog);
    }

    [Fact]
    public async Task Audit_CompleteTask_failure_does_not_append_an_entry()
    {
        var type = BuildType(tasksByState: new()
        {
            ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
        });
        var workItem = await SeedAsync();

        var result = await BuildService(type).CompleteTaskAsync(
            workItem.Id, "unknown-task", AuditUser(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var fetched = await GetAsync(workItem.Id);
        Assert.Empty(fetched.AuditLog);
    }

    [Fact]
    public async Task Audit_ApplyAction_records_from_and_to_state()
    {
        var type = BuildType(transitions: [
            new WorkItemTransition("withdraw", "Withdraw", "submitted", "rejected", RequiresAllTasksComplete: false)
        ]);
        var workItem = await SeedAsync();

        await BuildService(type).ApplyActionAsync(
            workItem.Id, "withdraw", AuditUser(), TestContext.Current.CancellationToken);

        var fetched = await GetAsync(workItem.Id);
        var entry = Assert.Single(fetched.AuditLog);
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
        var workItem = await SeedAsync();

        var result = await BuildService(type).ApplyActionAsync(
            workItem.Id, "approve", AuditUser(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var fetched = await GetAsync(workItem.Id);
        Assert.Empty(fetched.AuditLog);
    }

    [Fact]
    public async Task Audit_Assign_records_assignee_and_previous_assignee()
    {
        var type = BuildType();
        var workItem = await SeedAsync(configure: w =>
        {
            w.AssignedToId = "bob-1";
            w.AssignedToName = "Bob Example";
        });

        var actor = UserWithRoles("alice-1", WorkItemService.AssignRole);
        await BuildService(type).AssignAsync(
            workItem.Id, "carol-1", "Carol Example", actor, TestContext.Current.CancellationToken);

        var fetched = await GetAsync(workItem.Id);
        var entry = Assert.Single(fetched.AuditLog);
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
        var workItem = await SeedAsync(configure: w =>
        {
            w.AssignedToId = "alice-1";
            w.AssignedToName = "Alice";
        });

        var actor = UserWithRoles("actor-1", WorkItemService.AssignRole);
        await BuildService(type).AssignAsync(
            workItem.Id, "alice-1", "Alice", actor, TestContext.Current.CancellationToken);

        var fetched = await GetAsync(workItem.Id);
        Assert.Empty(fetched.AuditLog);
    }

    [Fact]
    public async Task Audit_Assign_authorization_failure_does_not_append_an_entry()
    {
        var type = BuildType();
        var workItem = await SeedAsync();

        var actor = UserWithRoles("alice-1", "standard");
        var result = await BuildService(type).AssignAsync(
            workItem.Id, "bob-1", "Bob", actor, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var fetched = await GetAsync(workItem.Id);
        Assert.Empty(fetched.AuditLog);
    }

    [Fact]
    public async Task Audit_Unassign_records_previous_assignee()
    {
        var type = BuildType();
        var workItem = await SeedAsync(configure: w =>
        {
            w.AssignedToId = "alice-1";
            w.AssignedToName = "Alice";
        });

        var actor = UserWithRoles("actor-1", WorkItemService.AssignRole);
        await BuildService(type).UnassignAsync(workItem.Id, actor, TestContext.Current.CancellationToken);

        var fetched = await GetAsync(workItem.Id);
        var entry = Assert.Single(fetched.AuditLog);
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
        var workItem = await SeedAsync();

        var actor = UserWithRoles("actor-1", WorkItemService.AssignRole);
        await BuildService(type).UnassignAsync(workItem.Id, actor, TestContext.Current.CancellationToken);

        var fetched = await GetAsync(workItem.Id);
        Assert.Empty(fetched.AuditLog);
    }

    [Fact]
    public async Task Audit_AddNote_records_note_id()
    {
        var type = BuildType();
        var workItem = await SeedAsync();

        await BuildService(type).AddNoteAsync(
            workItem.Id, "  A note.  ", AuditUser(), TestContext.Current.CancellationToken);

        var fetched = await GetAsync(workItem.Id);
        var note = Assert.Single(fetched.Notes);
        var entry = Assert.Single(fetched.AuditLog);
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
        var workItem = await SeedAsync();

        var result = await BuildService(type).AddNoteAsync(
            workItem.Id, "   ", AuditUser(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var fetched = await GetAsync(workItem.Id);
        Assert.Empty(fetched.AuditLog);
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
        var workItem = await SeedAsync();

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

        var fetched = await GetAsync(workItem.Id);
        Assert.Equal(3, fetched.AuditLog.Count);
        Assert.Equal(["note-added", "task-completed", "action-applied"],
            fetched.AuditLog.Select(e => e.Action).ToArray());
        // Strictly increasing timestamps — entries are appended in
        // chronological (insertion) order on disk.
        Assert.True(fetched.AuditLog[0].CreatedAt < fetched.AuditLog[1].CreatedAt);
        Assert.True(fetched.AuditLog[1].CreatedAt < fetched.AuditLog[2].CreatedAt);
    }

    private sealed class MutableTimeProvider(DateTime initial) : TimeProvider
    {
        private DateTime _now = initial;
        public override DateTimeOffset GetUtcNow() => new(_now, TimeSpan.Zero);
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    // ---------------------- SubmitAsync (RA-97 birth event) ----------------------

    [Fact]
    public async Task Submit_persists_work_item_with_initial_state_and_template_snapshot()
    {
        var type = BuildType();
        var payload = new BsonDocument { ["foo"] = "bar" };

        var result = await BuildService(type).SubmitAsync(
            type, payload, "test-client", AuditUser(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkItem);
        var fetched = await GetAsync(result.WorkItem!.Id);
        Assert.Equal(TypeId, fetched.TypeId);
        Assert.Equal("submitted", fetched.StateId);
        Assert.Equal("test-client", fetched.SubmittedBy);
        Assert.Equal(TickedNow, fetched.SubmittedAt);
        Assert.Equal(TickedNow, fetched.LastModifiedAt);
        Assert.NotNull(fetched.TemplateSnapshot);
        Assert.Equal("v1", fetched.TemplateVersion);
        Assert.Equal("v1", fetched.TemplateSnapshot!.TemplateVersion);
        Assert.Equal("bar", fetched.Payload["foo"].AsString);
    }

    [Fact]
    public async Task Submit_appends_single_work_item_submitted_audit_entry_in_same_create_call()
    {
        var type = BuildType();

        var result = await BuildService(type).SubmitAsync(
            type, new BsonDocument(), "test-client", AuditUser(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkItem);
        var fetched = await GetAsync(result.WorkItem!.Id);
        // The audit entry must have been part of the original CreateAsync
        // write (not a follow-up replace), so Version is still 0.
        Assert.Equal(0, fetched.Version);
        var entry = Assert.Single(fetched.AuditLog);
        Assert.Equal("work-item-submitted", entry.Action);
        Assert.Equal("Work item submitted", entry.ActionDisplayName);
        Assert.Equal("alice-1", entry.CreatedBy);
        Assert.Equal("Alice Example", entry.CreatedByName);
        Assert.Equal(fetched.SubmittedAt, entry.CreatedAt);
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
            UserWithoutActorId(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.MissingActorIdentity, result.FailureCode);

        // No document was created: the database is empty for this type.
        var page = await _persistence.QueryAsync(
            new WorkItemQuery(TypeIds: [TypeId], Page: 1, PageSize: 10),
            TestContext.Current.CancellationToken);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Submit_records_submission_metadata_on_birth_audit_entry()
    {
        // RA-126: source / clientId / userId / applicationReference are
        // appended to the birth entry's Details alongside the existing
        // typeId / stateId / templateVersion keys. CreatedAt must be the
        // server-side receipt time from the injected TimeProvider, not a
        // client-supplied value.
        var type = BuildType();
        var metadata = new Dictionary<string, string?>
        {
            ["source"] = "operator-fe",
            ["applicationReference"] = "APP-123"
        };

        var result = await BuildService(type).SubmitAsync(
            type, new BsonDocument(), "test-client", AuditUser(),
            submissionMetadata: metadata,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var fetched = await GetAsync(result.WorkItem!.Id);
        var entry = Assert.Single(fetched.AuditLog);
        Assert.Equal("work-item-submitted", entry.Action);
        Assert.Equal(TickedNow, entry.CreatedAt);
        Assert.Equal(TypeId, entry.Details["typeId"]);
        Assert.Equal("submitted", entry.Details["stateId"]);
        Assert.Equal("v1", entry.Details["templateVersion"]);
        Assert.Equal("operator-fe", entry.Details["source"]);
        Assert.Equal("test-client", entry.Details["clientId"]);
        Assert.Equal("alice-1", entry.Details["userId"]);
        Assert.Equal("APP-123", entry.Details["applicationReference"]);
    }

    [Fact]
    public async Task Submit_records_null_metadata_keys_when_no_metadata_supplied()
    {
        // RA-126: when the caller passes no metadata the four keys still
        // appear on Details. clientId / userId resolve from claims;
        // source / applicationReference are null.
        var type = BuildType();

        var result = await BuildService(type).SubmitAsync(
            type, new BsonDocument(), "test-client", AuditUser(),
            submissionMetadata: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var fetched = await GetAsync(result.WorkItem!.Id);
        var entry = Assert.Single(fetched.AuditLog);
        Assert.True(entry.Details.ContainsKey("source"));
        Assert.Null(entry.Details["source"]);
        Assert.True(entry.Details.ContainsKey("applicationReference"));
        Assert.Null(entry.Details["applicationReference"]);
        Assert.Equal("test-client", entry.Details["clientId"]);
        Assert.Equal("alice-1", entry.Details["userId"]);
    }

    /// <summary>
    /// Produces a service whose persistence wraps the real one so that a
    /// competing writer bumps the on-disk version between the engine's
    /// load and replace, triggering the optimistic-concurrency exception
    /// for real (no mocked throws — see epr-efp).
    /// </summary>
    private WorkItemService BuildRacingService(IWorkItemType type, Guid id)
    {
        var racing = new RacingPersistence(_persistence, () =>
        {
            var raceLoaded = _persistence.GetByIdAsync(id).GetAwaiter().GetResult();
            raceLoaded!.LastModifiedAt = raceLoaded.LastModifiedAt.AddMinutes(1);
            _persistence.ReplaceAsync(raceLoaded).GetAwaiter().GetResult();
        });
        return new WorkItemService(
            new WorkItemRegistry([type]),
            racing,
            NullLogger<WorkItemService>.Instance,
            _time);
    }

    private sealed class RacingPersistence(IWorkItemPersistence inner, Action onBeforeReplace) : IWorkItemPersistence
    {
        public Task CreateAsync(WorkItem workItem, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(workItem, cancellationToken);

        public Task<bool> CreateIfAbsentAsync(WorkItem workItem, CancellationToken cancellationToken = default) =>
            inner.CreateIfAbsentAsync(workItem, cancellationToken);

        public Task<WorkItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetByIdAsync(id, cancellationToken);

        public Task<WorkItemPage> QueryAsync(WorkItemQuery query, CancellationToken cancellationToken = default) =>
            inner.QueryAsync(query, cancellationToken);

        public Task ReplaceAsync(WorkItem workItem, CancellationToken cancellationToken = default)
        {
            onBeforeReplace();
            return inner.ReplaceAsync(workItem, cancellationToken);
        }
    }
}
