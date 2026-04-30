using System.Security.Claims;
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
/// </summary>
public class WorkItemServiceCompoundTests
{
    private const string TypeId = "test-type";
    private static readonly DateTime InitialNow = new(2026, 4, 27, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TickedNow = InitialNow.AddMinutes(5);

    private readonly IWorkItemPersistence _persistence = Substitute.For<IWorkItemPersistence>();
    private readonly CompoundFakeTimeProvider _time = new(TickedNow);

    private WorkItemService BuildService(IWorkItemType type) =>
        new(
            new WorkItemRegistry([type]),
            _persistence,
            NullLogger<WorkItemService>.Instance,
            _time);

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

    private static WorkItem ExistingWorkItem(Dictionary<string, HashSet<string>>? completed = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedAt = InitialNow,
            LastModifiedAt = InitialNow,
            SubmittedBy = "test-client",
            CompletedTaskIdsByState = completed ?? new()
        };

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
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(BuildType()).AddNoteAndCompleteTaskAsync(
            workItem.Id, "record-rationale", "Approved — meets all criteria.",
            User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);

        // Single document write — atomic.
        await _persistence.Received(1).ReplaceAsync(workItem, Arg.Any<CancellationToken>());

        // Both mutations are present.
        var note = Assert.Single(workItem.Notes);
        Assert.Equal("Approved — meets all criteria.", note.Text);
        Assert.Equal("alice-1", note.CreatedBy);
        Assert.Equal("Alice Example", note.CreatedByName);
        Assert.Equal(TickedNow, note.CreatedAt);

        Assert.Contains("record-rationale", workItem.CompletedTaskIdsByState["submitted"]);
        Assert.Equal(TickedNow, workItem.LastModifiedAt);

        // Two audit entries — one per logical mutation — both attributed to
        // the same actor at the same instant.
        Assert.Equal(2, workItem.AuditLog.Count);
        var noteAudit = Assert.Single(workItem.AuditLog, a => a.Action == "note-added");
        var taskAudit = Assert.Single(workItem.AuditLog, a => a.Action == "task-completed");
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
        // The note write is the caller's primary intent — assessors may be
        // amending a rationale — so the note still goes in, while the
        // completion half is treated as an idempotent no-op (same contract
        // as CompleteTaskAsync). Documents this design choice.
        var workItem = ExistingWorkItem(completed: new()
        {
            ["submitted"] = ["record-rationale"]
        });
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(BuildType()).AddNoteAndCompleteTaskAsync(
            workItem.Id, "record-rationale", "Updated rationale.",
            User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);
        await _persistence.Received(1).ReplaceAsync(workItem, Arg.Any<CancellationToken>());
        Assert.Single(workItem.Notes);
        Assert.Single(workItem.AuditLog);
        Assert.Equal("note-added", workItem.AuditLog[0].Action);
    }

    [Fact]
    public async Task Unknown_task_id_aborts_before_any_mutation()
    {
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(BuildType()).AddNoteAndCompleteTaskAsync(
            workItem.Id, "task-that-does-not-apply", "Some valid note text.",
            User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.TaskNotApplicable, result.FailureCode);

        // The whole point of the compound path: a validation failure leaves
        // the document untouched. No note, no completion, no audit, no write.
        Assert.Empty(workItem.Notes);
        Assert.Empty(workItem.AuditLog);
        Assert.False(workItem.CompletedTaskIdsByState.ContainsKey("submitted"));
        await _persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Concurrency_conflict_on_replace_surfaces_typed_failure()
    {
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);
        _persistence
            .When(p => p.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new WorkItemConcurrencyException(workItem.Id, 0));

        var result = await BuildService(BuildType()).AddNoteAndCompleteTaskAsync(
            workItem.Id, "record-rationale", "Some valid note text.",
            User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.FailureCode);

        // Only one write was attempted. The on-disk document is untouched —
        // we asserted that via the "ReplaceAsync threw before mutating
        // Mongo" fake; the in-memory workItem will have been mutated by the
        // engine, but persistence's failure means nothing was committed.
        await _persistence.Received(1).ReplaceAsync(workItem, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Blank_note_text_is_rejected_without_loading_the_document()
    {
        var result = await BuildService(BuildType()).AddNoteAndCompleteTaskAsync(
            Guid.NewGuid(), "record-rationale", "   ",
            User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidNote, result.FailureCode);
        await _persistence.DidNotReceiveWithAnyArgs().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
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
        _persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var result = await BuildService(BuildType()).AddNoteAndCompleteTaskAsync(
            Guid.NewGuid(), "record-rationale", "Some valid note text.",
            User(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
        await _persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
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
        await _persistence.DidNotReceiveWithAnyArgs().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private sealed class CompoundFakeTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
