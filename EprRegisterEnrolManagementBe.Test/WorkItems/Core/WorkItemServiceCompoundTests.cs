using System.Security.Claims;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// Tests for <see cref="IWorkItemService.AddNoteAndCompleteTaskAsync"/> —
/// the framework's atomic "append a note and mark a task complete" path
/// added to fix the orphan-note bug that arose when an endpoint called
/// <see cref="IWorkItemService.AddNoteAsync"/> followed by
/// <see cref="IWorkItemService.CompleteTaskAsync"/> separately and the
/// second call failed (concurrency conflict / Mongo blip) leaving the note
/// persisted against an unfinished task.
///
/// epr-efp: backed by ephemeral MongoDB so atomicity claims are
/// validated against the real driver. Concurrency conflicts are
/// induced naturally via stale-load races rather than mocked.
/// </summary>
public class WorkItemServiceCompoundTests : IAsyncDisposable
{
    private const string TypeId = "test-type";
    private static readonly DateTime InitialNow = new(2026, 4, 27, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TickedNow = InitialNow.AddMinutes(5);

    private readonly TestMongoDbClientFactory _clientFactory;
    private readonly string _databaseName;
    private readonly WorkItemPersistence _persistence;
    private readonly CompoundFakeTimeProvider _time = new(TickedNow);

    public WorkItemServiceCompoundTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("svc_compound");
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

    private WorkItemService BuildServiceWithTaskHooks(IWorkItemType type, params IWorkItemPostTaskHook[] hooks) =>
        new(
            new WorkItemRegistry([type]),
            _persistence,
            NullLogger<WorkItemService>.Instance,
            _time,
            postTaskHooks: hooks);

    private static TestWorkItemType BuildType() =>
        new(
            TypeId,
            "Test type",
            initialState: new WorkItemState("submitted", "Submitted"),
            states: [new WorkItemState("submitted", "Submitted")],
            tasksByState: new Dictionary<string, IReadOnlyCollection<WorkItemTask>>
            {
                ["submitted"] = [new WorkItemTask("record-rationale", "Record rationale")]
            },
            transitions: null);

    private async Task<WorkItem> SeedAsync(Dictionary<string, HashSet<string>>? completed = null)
    {
        var workItem = new WorkItem
        {
            Id = Guid.NewGuid(),
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedAt = InitialNow,
            LastModifiedAt = InitialNow,
            SubmittedBy = "test-client"
        };
        if (completed is not null)
        {
            foreach (var (state, tasks) in completed)
            {
                workItem.CompletedTaskIdsByState[state] = new HashSet<string>(tasks, StringComparer.OrdinalIgnoreCase);
            }
        }
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
            new Claim("user:id", "alice-1"),
            new Claim("user:name", "Alice Example")
        ], "test"));

    [Fact]
    public async Task Persists_note_and_completion_in_a_single_replace_with_two_audit_entries()
    {
        var workItem = await SeedAsync();

        var result = await BuildService(BuildType()).AddNoteAndCompleteTaskAsync(
            workItem.Id, "record-rationale", "Approved — meets all criteria.",
            User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);

        var fetched = await GetAsync(workItem.Id);

        // Single document write — atomic. Optimistic-concurrency Version
        // bumps once for the compound replace.
        Assert.Equal(1, fetched.Version);

        var note = Assert.Single(fetched.Notes);
        Assert.Equal("Approved — meets all criteria.", note.Text);
        Assert.Equal("alice-1", note.CreatedBy);
        Assert.Equal("Alice Example", note.CreatedByName);
        Assert.Equal(TickedNow, note.CreatedAt);

        Assert.Contains("record-rationale", fetched.CompletedTaskIdsByState["submitted"]);
        Assert.Equal(TickedNow, fetched.LastModifiedAt);

        // Two audit entries — one per logical mutation — both attributed to
        // the same actor at the same instant.
        Assert.Equal(2, fetched.AuditLog.Count);
        var noteAudit = Assert.Single(fetched.AuditLog, a => a.Action == "note-added");
        var taskAudit = Assert.Single(fetched.AuditLog, a => a.Action == "task-completed");
        Assert.Equal("alice-1", noteAudit.CreatedBy);
        Assert.Equal("alice-1", taskAudit.CreatedBy);
        Assert.Equal(note.Id.ToString(), noteAudit.Details["noteId"]);
        // epr-27o: the audit entry includes the trimmed note body so the
        // audit log is self-describing.
        Assert.Equal("Approved — meets all criteria.", noteAudit.Details["noteText"]);
        Assert.Equal(note.Text, noteAudit.Details["noteText"]);
        Assert.Equal("record-rationale", taskAudit.Details["taskId"]);
    }

    [Fact]
    public async Task Already_complete_task_still_writes_the_note_but_emits_no_completion_audit()
    {
        var workItem = await SeedAsync(completed: new()
        {
            ["submitted"] = ["record-rationale"]
        });

        var result = await BuildService(BuildType()).AddNoteAndCompleteTaskAsync(
            workItem.Id, "record-rationale", "Updated rationale.",
            User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);
        var fetched = await GetAsync(workItem.Id);
        Assert.Single(fetched.Notes);
        Assert.Single(fetched.AuditLog);
        Assert.Equal("note-added", fetched.AuditLog[0].Action);
    }

    [Fact]
    public async Task Unknown_task_id_aborts_before_any_mutation()
    {
        var workItem = await SeedAsync();

        var result = await BuildService(BuildType()).AddNoteAndCompleteTaskAsync(
            workItem.Id, "task-that-does-not-apply", "Some valid note text.",
            User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.TaskNotApplicable, result.FailureCode);

        // The whole point of the compound path: a validation failure leaves
        // the document untouched. No note, no completion, no audit, no
        // version bump.
        var fetched = await GetAsync(workItem.Id);
        Assert.Empty(fetched.Notes);
        Assert.Empty(fetched.AuditLog);
        Assert.False(fetched.CompletedTaskIdsByState.ContainsKey("submitted"));
        Assert.Equal(0, fetched.Version);
    }

    [Fact]
    public async Task Concurrency_conflict_on_replace_surfaces_typed_failure()
    {
        // Real concurrency conflict: load the document in one service
        // call (which captures Version 0), persist a competing change
        // through a second service call (which bumps to Version 1),
        // then race a stale write that should fail.
        var workItem = await SeedAsync();

        // Hand-craft the stale path by going under the engine: a
        // sibling service mutates the on-disk document, raising the
        // version. Then our service call's load-modify-write sees a
        // version-0 document but the disk has version 1 by the time
        // it tries to persist.
        // Easiest deterministic shape: load + mutate + persist via a
        // second persistence handle, then call AddNoteAndCompleteTask
        // from the test's persistence — the engine loads its own copy
        // (now at v1), but we pre-empt it by mutating to v2 BETWEEN
        // its load and replace via a small interceptor.
        var racingPersistence = new RacingPersistence(_persistence, () =>
        {
            // Bump the on-disk version under the engine's feet.
            var raceLoaded = _persistence.GetByIdAsync(workItem.Id).GetAwaiter().GetResult();
            raceLoaded!.LastModifiedAt = raceLoaded.LastModifiedAt.AddMinutes(1);
            _persistence.ReplaceAsync(raceLoaded).GetAwaiter().GetResult();
        });
        var racingService = new WorkItemService(
            new WorkItemRegistry([BuildType()]),
            racingPersistence,
            NullLogger<WorkItemService>.Instance,
            _time);

        var result = await racingService.AddNoteAndCompleteTaskAsync(
            workItem.Id, "record-rationale", "Some valid note text.",
            User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.FailureCode);

        // The on-disk document only carries the racing write, not the
        // engine's stale write. No orphan note, no orphan audit entry.
        var fetched = await GetAsync(workItem.Id);
        Assert.Empty(fetched.Notes);
        Assert.Empty(fetched.AuditLog);
    }

    [Fact]
    public async Task Blank_note_text_is_rejected_without_loading_the_document()
    {
        var result = await BuildService(BuildType()).AddNoteAndCompleteTaskAsync(
            Guid.NewGuid(), "record-rationale", "   ",
            User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidNote, result.FailureCode);
    }

    [Fact]
    public async Task Note_text_over_max_length_is_rejected()
    {
        var result = await BuildService(BuildType()).AddNoteAndCompleteTaskAsync(
            Guid.NewGuid(), "record-rationale",
            new string('x', WorkItemService.MaxNoteLength + 1),
            User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidNote, result.FailureCode);
    }

    [Fact]
    public async Task Returns_not_found_when_work_item_missing()
    {
        var result = await BuildService(BuildType()).AddNoteAndCompleteTaskAsync(
            Guid.NewGuid(), "record-rationale", "Some valid note text.",
            User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task Missing_actor_identity_is_rejected_before_loading_the_document()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("cognito:client_id", "test-client")], "test"));

        var result = await BuildService(BuildType()).AddNoteAndCompleteTaskAsync(
            Guid.NewGuid(), "record-rationale", "Some valid note text.",
            anonymous, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.MissingActorIdentity, result.FailureCode);
    }

    [Fact]
    public async Task AddNoteAndCompleteTaskAsync_fires_task_hooks_when_last_task_completed()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = await SeedAsync();
        var hook = Substitute.For<IWorkItemPostTaskHook>();

        var result = await BuildServiceWithTaskHooks(BuildType(), hook).AddNoteAndCompleteTaskAsync(
            workItem.Id, "record-rationale", "All criteria met.",
            User(), ct);

        Assert.True(result.IsSuccess, result.Message);
        await hook.Received(1).OnAllTasksCompletedAsync(
            Arg.Is<WorkItem>(w => w.Id == workItem.Id),
            "submitted",
            Arg.Any<ClaimsPrincipal>(),
            ct);
    }

    private sealed class CompoundFakeTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    /// <summary>
    /// Wraps real persistence and runs <paramref name="onBeforeReplace"/>
    /// just before delegating to <see cref="ReplaceAsync"/>. Used to
    /// race a competing writer in between an engine call's load and
    /// replace so the test exercises the real optimistic-concurrency
    /// path rather than mocking the exception out of the persistence
    /// stub.
    /// </summary>
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
