using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-372: the engine's "effective task state" seam. A module may declare that
/// the tasks applying to a work item belong to a state other than the one it
/// is sitting in (re-accreditation's <c>updated</c> waypoint is why this
/// exists). These tests pin the core contract independently of any module:
/// the redirect governs task projection, task lookup, the per-state completion
/// bucket and the "all tasks complete" gate — but never which actions are
/// available — and a resolver that abstains, misbehaves or throws degrades
/// silently to the item's own state.
/// </summary>
public class WorkItemTaskStateResolverTests
{
    private const string TypeId = "test-type";
    private const string ActualState = "waypoint";
    private const string OriginState = "review";

    private static readonly WorkItemState[] s_states =
    [
        new(OriginState, "Review"),
        new(ActualState, "Waypoint"),
        new("done", "Done", IsTerminal: true),
    ];

    private static readonly WorkItemTask s_first = new("first-task", "First task");
    private static readonly WorkItemTask s_second = new("second-task", "Second task");

    /// <summary>
    /// The origin state owns two tasks; the waypoint owns none — exactly the
    /// shape that made RA-372 present as an empty checklist.
    /// </summary>
    private static TestWorkItemType BuildType(WorkItemTransition[]? transitions = null) =>
        new(
            TypeId,
            "Test type",
            initialState: s_states[0],
            states: s_states,
            tasksByState: new() { [OriginState] = [s_first, s_second] },
            transitions: transitions
        );

    private static WorkItem BuildWorkItem(string stateId = ActualState) =>
        new()
        {
            TypeId = TypeId,
            StateId = stateId,
            SubmittedBy = "test-client",
        };

    private static WorkItemService BuildService(
        IWorkItemType type,
        IWorkItemPersistence persistence,
        IWorkItemTaskStateResolver[]? resolvers = null,
        IWorkItemPostTaskHook[]? postTaskHooks = null
    ) =>
        new(
            new WorkItemRegistry([type]),
            persistence,
            NullLogger<WorkItemService>.Instance,
            postTaskHooks: postTaskHooks,
            taskStateResolvers: resolvers
        );

    private static IWorkItemPersistence PersistenceFor(WorkItem workItem)
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);
        return persistence;
    }

    private static ClaimsPrincipal User() =>
        new(
            new ClaimsIdentity(
                [new Claim("cognito:client_id", "test-client"), new Claim("user:id", "test-user")],
                "test"
            )
        );

    // ------------------------------- projection -------------------------------

    [Fact]
    public void Project_uses_the_items_own_state_when_no_resolvers_are_registered()
    {
        var service = BuildService(BuildType(), Substitute.For<IWorkItemPersistence>());

        var projection = service.Project(BuildWorkItem());

        // Pre-RA-372 behaviour: the waypoint declares no tasks, so none show.
        Assert.Empty(projection.Tasks);
    }

    [Theory]
    // Abstaining outright, and the degenerate strings that mean "no opinion".
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Project_uses_the_items_own_state_when_the_resolver_abstains(string? resolved)
    {
        var service = BuildService(
            BuildType(),
            Substitute.For<IWorkItemPersistence>(),
            [new StubResolver(resolved)]
        );

        var projection = service.Project(BuildWorkItem());

        Assert.Empty(projection.Tasks);
    }

    [Fact]
    public void Project_ignores_a_resolver_that_names_a_state_the_template_does_not_declare()
    {
        // A stale resolver must not be able to blank out a real checklist by
        // pointing at a state that no longer exists.
        var service = BuildService(
            BuildType(),
            Substitute.For<IWorkItemPersistence>(),
            [new StubResolver("a-state-that-does-not-exist")]
        );

        var projection = service.Project(BuildWorkItem(OriginState));

        Assert.Equal([s_first.Id, s_second.Id], projection.Tasks.Select(t => t.TaskId));
    }

    [Fact]
    public void Project_falls_back_to_the_items_own_state_when_a_resolver_throws()
    {
        // Resolvers run on every read; a buggy one must not take the
        // worklist down with it.
        var service = BuildService(
            BuildType(),
            Substitute.For<IWorkItemPersistence>(),
            [new ThrowingResolver()]
        );

        var projection = service.Project(BuildWorkItem(OriginState));

        Assert.Equal([s_first.Id, s_second.Id], projection.Tasks.Select(t => t.TaskId));
    }

    [Fact]
    public void Project_returns_the_resolved_states_tasks()
    {
        var service = BuildService(
            BuildType(),
            Substitute.For<IWorkItemPersistence>(),
            [new StubResolver(OriginState)]
        );

        var projection = service.Project(BuildWorkItem());

        Assert.Equal([s_first.Id, s_second.Id], projection.Tasks.Select(t => t.TaskId));
        Assert.Equal([s_first.DisplayName, s_second.DisplayName], projection.Tasks.Select(t => t.DisplayName));
    }

    [Fact]
    public void Project_uses_the_first_resolver_that_has_an_opinion()
    {
        var service = BuildService(
            BuildType(),
            Substitute.For<IWorkItemPersistence>(),
            [new ThrowingResolver(), new StubResolver(null), new StubResolver(OriginState), new StubResolver("done")]
        );

        var projection = service.Project(BuildWorkItem());

        Assert.Equal([s_first.Id, s_second.Id], projection.Tasks.Select(t => t.TaskId));
    }

    [Fact]
    public void Project_reads_progress_from_the_resolved_states_bucket()
    {
        // AC2: work completed before the detour is still visible during it.
        var workItem = BuildWorkItem();
        workItem.CompletedTaskIdsByState[OriginState] = new(
            [s_first.Id],
            StringComparer.OrdinalIgnoreCase
        );
        var service = BuildService(
            BuildType(),
            Substitute.For<IWorkItemPersistence>(),
            [new StubResolver(OriginState)]
        );

        var projection = service.Project(workItem);

        Assert.True(projection.Tasks.Single(t => t.TaskId == s_first.Id).IsComplete);
        Assert.False(projection.Tasks.Single(t => t.TaskId == s_second.Id).IsComplete);
    }

    [Fact]
    public void Project_prefers_the_per_task_status_map_of_the_resolved_state()
    {
        var workItem = BuildWorkItem();
        workItem.TaskStatusesByState[OriginState] = new(StringComparer.OrdinalIgnoreCase)
        {
            [s_first.Id] = WorkItemTaskStatus.Blocked,
        };
        var service = BuildService(
            BuildType(),
            Substitute.For<IWorkItemPersistence>(),
            [new StubResolver(OriginState)]
        );

        var projection = service.Project(workItem);

        Assert.Equal(WorkItemTaskStatus.Blocked, projection.Tasks.Single(t => t.TaskId == s_first.Id).Status);
    }

    [Fact]
    public void Project_matches_available_actions_on_the_actual_state_not_the_resolved_one()
    {
        // The redirect answers "what work is outstanding", never "where can
        // this item go" — otherwise a caller could invoke an action from a
        // state the item is not in.
        var type = BuildType(
            [
                new WorkItemTransition("leave-waypoint", "Leave", ActualState, OriginState),
                new WorkItemTransition("finish-review", "Finish", OriginState, "done"),
            ]
        );
        var workItem = BuildWorkItem();
        workItem.CompletedTaskIdsByState[OriginState] = new(
            [s_first.Id, s_second.Id],
            StringComparer.OrdinalIgnoreCase
        );
        var service = BuildService(
            type,
            Substitute.For<IWorkItemPersistence>(),
            [new StubResolver(OriginState)]
        );

        var projection = service.Project(workItem);

        Assert.Equal(["leave-waypoint"], projection.AvailableActions.Select(a => a.ActionId));
    }

    // ------------------------------- mutation -------------------------------

    [Fact]
    public async Task CompleteTask_records_the_completion_against_the_resolved_state()
    {
        // AC3: the completion lands in the origin state's bucket, which is
        // what makes it survive the return trip.
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var service = BuildService(
            BuildType(),
            PersistenceFor(workItem),
            [new StubResolver(OriginState)]
        );

        var result = await service.CompleteTaskAsync(workItem.Id, s_first.Id, User(), ct);

        Assert.True(result.IsSuccess);
        Assert.Contains(s_first.Id, workItem.CompletedTaskIdsByState[OriginState]);
        Assert.False(workItem.CompletedTaskIdsByState.ContainsKey(ActualState));
        Assert.Equal(
            OriginState,
            workItem.AuditLog.Single(e => e.Action == "task-completed").Details["stateId"]
        );
    }

    [Fact]
    public async Task CompleteTask_is_an_idempotent_replay_against_the_resolved_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        workItem.CompletedTaskIdsByState[OriginState] = new(
            [s_first.Id],
            StringComparer.OrdinalIgnoreCase
        );
        var service = BuildService(
            BuildType(),
            PersistenceFor(workItem),
            [new StubResolver(OriginState)]
        );

        var result = await service.CompleteTaskAsync(workItem.Id, s_first.Id, User(), ct);

        Assert.True(result.IsIdempotentReplay);
        Assert.Empty(workItem.AuditLog);
    }

    [Fact]
    public async Task CompleteTask_rejects_a_task_belonging_to_neither_the_actual_nor_resolved_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var service = BuildService(
            BuildType(),
            PersistenceFor(workItem),
            [new StubResolver(OriginState)]
        );

        var result = await service.CompleteTaskAsync(workItem.Id, "not-a-task", User(), ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.TaskNotApplicable, result.FailureCode);
        // The message names where the item actually is, which is what a
        // support engineer reading it needs.
        Assert.Contains(ActualState, result.Message);
    }

    [Fact]
    public async Task SetTaskStatus_records_the_status_against_the_resolved_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var service = BuildService(
            BuildType(),
            PersistenceFor(workItem),
            [new StubResolver(OriginState)]
        );

        var result = await service.SetTaskStatusAsync(
            workItem.Id,
            s_first.Id,
            WorkItemTaskStatus.InProgress,
            User(),
            ct
        );

        Assert.True(result.IsSuccess);
        Assert.Equal(
            WorkItemTaskStatus.InProgress,
            workItem.TaskStatusesByState[OriginState][s_first.Id]
        );
        Assert.False(workItem.TaskStatusesByState.ContainsKey(ActualState));
        Assert.Equal(
            OriginState,
            workItem.AuditLog.Single(e => e.Action == "task-status-changed").Details["stateId"]
        );
    }

    [Fact]
    public async Task SetTaskStatus_is_a_no_op_when_the_resolved_state_already_holds_that_status()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        workItem.TaskStatusesByState[OriginState] = new(StringComparer.OrdinalIgnoreCase)
        {
            [s_first.Id] = WorkItemTaskStatus.Blocked,
        };
        var service = BuildService(
            BuildType(),
            PersistenceFor(workItem),
            [new StubResolver(OriginState)]
        );

        var result = await service.SetTaskStatusAsync(
            workItem.Id,
            s_first.Id,
            WorkItemTaskStatus.Blocked,
            User(),
            ct
        );

        Assert.True(result.IsSuccess);
        Assert.Empty(workItem.AuditLog);
    }

    [Fact]
    public async Task SetTaskStatus_rejects_a_task_outside_the_resolved_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var service = BuildService(
            BuildType(),
            PersistenceFor(workItem),
            [new StubResolver(OriginState)]
        );

        var result = await service.SetTaskStatusAsync(
            workItem.Id,
            "not-a-task",
            WorkItemTaskStatus.Completed,
            User(),
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.TaskNotApplicable, result.FailureCode);
        Assert.Contains(ActualState, result.Message);
    }

    [Fact]
    public async Task AddNoteAndCompleteTask_records_the_completion_against_the_resolved_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var service = BuildService(
            BuildType(),
            PersistenceFor(workItem),
            [new StubResolver(OriginState)]
        );

        var result = await service.AddNoteAndCompleteTaskAsync(
            workItem.Id,
            s_first.Id,
            "a note",
            User(),
            ct
        );

        Assert.True(result.IsSuccess);
        Assert.Contains(s_first.Id, workItem.CompletedTaskIdsByState[OriginState]);
        Assert.Equal(
            OriginState,
            workItem.AuditLog.Single(e => e.Action == "task-completed").Details["stateId"]
        );
    }

    [Fact]
    public async Task AddNoteAndCompleteTask_rejects_a_task_outside_the_resolved_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var service = BuildService(
            BuildType(),
            PersistenceFor(workItem),
            [new StubResolver(OriginState)]
        );

        var result = await service.AddNoteAndCompleteTaskAsync(
            workItem.Id,
            "not-a-task",
            "a note",
            User(),
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.TaskNotApplicable, result.FailureCode);
        Assert.Empty(workItem.Notes);
    }

    // ------------------------------- gates & hooks -------------------------------

    [Fact]
    public async Task ApplyAction_gates_on_the_resolved_states_outstanding_tasks()
    {
        var ct = TestContext.Current.CancellationToken;
        var type = BuildType(
            [new WorkItemTransition("leave-waypoint", "Leave", ActualState, "done")]
        );
        var workItem = BuildWorkItem();
        var service = BuildService(type, PersistenceFor(workItem), [new StubResolver(OriginState)]);

        var result = await service.ApplyActionAsync(workItem.Id, "leave-waypoint", User(), ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.IncompleteTasks, result.FailureCode);
        Assert.Equal(ActualState, workItem.StateId);
    }

    [Fact]
    public async Task ApplyAction_passes_the_gate_once_the_resolved_states_tasks_are_complete()
    {
        var ct = TestContext.Current.CancellationToken;
        var type = BuildType(
            [new WorkItemTransition("leave-waypoint", "Leave", ActualState, "done")]
        );
        var workItem = BuildWorkItem();
        workItem.CompletedTaskIdsByState[OriginState] = new(
            [s_first.Id, s_second.Id],
            StringComparer.OrdinalIgnoreCase
        );
        var service = BuildService(type, PersistenceFor(workItem), [new StubResolver(OriginState)]);

        var result = await service.ApplyActionAsync(workItem.Id, "leave-waypoint", User(), ct);

        Assert.True(result.IsSuccess);
        Assert.Equal("done", workItem.StateId);
    }

    [Fact]
    public async Task Completing_the_last_task_fires_post_task_hooks_with_the_resolved_state()
    {
        // The hook is told which checklist was finished, not where the item
        // happens to be parked — see the rationale in CompleteTaskAsync.
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        workItem.CompletedTaskIdsByState[OriginState] = new(
            [s_first.Id],
            StringComparer.OrdinalIgnoreCase
        );
        var hook = new RecordingPostTaskHook();
        var service = BuildService(
            BuildType(),
            PersistenceFor(workItem),
            [new StubResolver(OriginState)],
            [hook]
        );

        await service.CompleteTaskAsync(workItem.Id, s_second.Id, User(), ct);

        Assert.Equal([OriginState], hook.StateIds);
    }

    [Fact]
    public async Task Completing_a_non_final_task_does_not_fire_post_task_hooks()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var hook = new RecordingPostTaskHook();
        var service = BuildService(
            BuildType(),
            PersistenceFor(workItem),
            [new StubResolver(OriginState)],
            [hook]
        );

        await service.CompleteTaskAsync(workItem.Id, s_first.Id, User(), ct);

        Assert.Empty(hook.StateIds);
    }

    [Fact]
    public async Task SetTaskStatus_to_completed_fires_post_task_hooks_with_the_resolved_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        workItem.CompletedTaskIdsByState[OriginState] = new(
            [s_first.Id],
            StringComparer.OrdinalIgnoreCase
        );
        var hook = new RecordingPostTaskHook();
        var service = BuildService(
            BuildType(),
            PersistenceFor(workItem),
            [new StubResolver(OriginState)],
            [hook]
        );

        await service.SetTaskStatusAsync(
            workItem.Id,
            s_second.Id,
            WorkItemTaskStatus.Completed,
            User(),
            ct
        );

        Assert.Equal([OriginState], hook.StateIds);
    }

    [Fact]
    public async Task AddNoteAndCompleteTask_fires_post_task_hooks_with_the_resolved_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        workItem.CompletedTaskIdsByState[OriginState] = new(
            [s_first.Id],
            StringComparer.OrdinalIgnoreCase
        );
        var hook = new RecordingPostTaskHook();
        var service = BuildService(
            BuildType(),
            PersistenceFor(workItem),
            [new StubResolver(OriginState)],
            [hook]
        );

        await service.AddNoteAndCompleteTaskAsync(workItem.Id, s_second.Id, "note", User(), ct);

        Assert.Equal([OriginState], hook.StateIds);
    }

    // ------------------------------- doubles -------------------------------

    private sealed class StubResolver(string? resolved, string typeId = TypeId)
        : IWorkItemTaskStateResolver
    {
        public string TypeId { get; } = typeId;

        public string? ResolveTaskStateId(WorkItem workItem, IWorkItemTemplate template) => resolved;
    }

    private sealed class ThrowingResolver : IWorkItemTaskStateResolver
    {
        public string TypeId => WorkItemTaskStateResolverTests.TypeId;

        public string? ResolveTaskStateId(WorkItem workItem, IWorkItemTemplate template) =>
            throw new InvalidOperationException("resolver is broken");
    }

    private sealed class RecordingPostTaskHook : IWorkItemPostTaskHook
    {
        public List<string> StateIds { get; } = [];

        public Task OnAllTasksCompletedAsync(
            WorkItem workItem,
            string stateId,
            ClaimsPrincipal user,
            CancellationToken cancellationToken
        )
        {
            StateIds.Add(stateId);
            return Task.CompletedTask;
        }
    }
}
