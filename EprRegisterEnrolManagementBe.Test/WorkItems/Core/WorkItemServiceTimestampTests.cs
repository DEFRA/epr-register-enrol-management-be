using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// Regression coverage for epr-bwk: every persisted timestamp on
/// <see cref="WorkItem"/>, <see cref="WorkItemNote"/> and
/// <see cref="WorkItemAuditEntry"/> must come from the injected
/// <see cref="TimeProvider"/>. The model types must NOT default their
/// timestamp properties to a wallclock value, otherwise tests that wire
/// up a <see cref="FakeTimeProvider"/> can be silently undermined when
/// the engine reads a model field it forgot to assign.
/// </summary>
public class WorkItemServiceTimestampTests
{
    private const string TypeId = "test-type";
    private static readonly DateTimeOffset T = new(2026, 4, 27, 10, 0, 0, TimeSpan.Zero);

    private readonly IWorkItemPersistence _persistence = Substitute.For<IWorkItemPersistence>();
    private readonly FakeTimeProvider _time = new(T);

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
                ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
            });

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity(
        [
            new Claim("cognito:client_id", "test-client"),
            new Claim("user:id", "alice-1"),
            new Claim("user:name", "Alice Example")
        ], "test"));

    [Fact]
    public async Task Submit_stamps_SubmittedAt_LastModifiedAt_and_birth_audit_from_TimeProvider()
    {
        WorkItem? captured = null;
        await _persistence.CreateAsync(Arg.Do<WorkItem>(w => captured = w), Arg.Any<CancellationToken>());

        var result = await BuildService(BuildType()).SubmitAsync(
            BuildType(), new BsonDocument(), submittedBy: "test-client",
            User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);
        Assert.NotNull(captured);

        var expected = T.UtcDateTime;
        Assert.Equal(expected, captured!.SubmittedAt);
        Assert.Equal(expected, captured.LastModifiedAt);
        var birth = Assert.Single(captured.AuditLog);
        Assert.Equal("work-item-submitted", birth.Action);
        Assert.Equal(expected, birth.CreatedAt);
    }

    [Fact]
    public async Task AddNote_records_CreatedAt_and_LastModifiedAt_from_advanced_TimeProvider()
    {
        var workItem = new WorkItem
        {
            Id = Guid.NewGuid(),
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedAt = T.UtcDateTime,
            LastModifiedAt = T.UtcDateTime,
            SubmittedBy = "test-client"
        };
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        _time.Advance(TimeSpan.FromMinutes(1));
        var expected = _time.GetUtcNow().UtcDateTime;

        var result = await BuildService(BuildType()).AddNoteAsync(
            workItem.Id, "Reviewed evidence.", User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);
        var note = Assert.Single(workItem.Notes);
        Assert.Equal(expected, note.CreatedAt);
        Assert.Equal(expected, workItem.LastModifiedAt);
        var audit = Assert.Single(workItem.AuditLog);
        Assert.Equal("note-added", audit.Action);
        Assert.Equal(expected, audit.CreatedAt);
    }

    [Fact]
    public async Task CompleteTask_records_LastModifiedAt_and_audit_timestamp_from_advanced_TimeProvider()
    {
        var workItem = new WorkItem
        {
            Id = Guid.NewGuid(),
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedAt = T.UtcDateTime,
            LastModifiedAt = T.UtcDateTime,
            SubmittedBy = "test-client"
        };
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        _time.Advance(TimeSpan.FromMinutes(2));
        var expected = _time.GetUtcNow().UtcDateTime;

        var result = await BuildService(BuildType()).CompleteTaskAsync(
            workItem.Id, "check-eligibility", User(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(expected, workItem.LastModifiedAt);
        var audit = Assert.Single(workItem.AuditLog);
        Assert.Equal("task-completed", audit.Action);
        Assert.Equal(expected, audit.CreatedAt);
    }

    [Fact]
    public void Default_construction_leaves_timestamps_at_MinValue_so_engine_bugs_are_loud()
    {
        // Regression for epr-bwk: the model types must NOT default their
        // timestamps to DateTime.UtcNow. A construction site that forgets
        // to pass a TimeProvider-derived value should fail loudly (a
        // MinValue stored in Mongo and read back) rather than silently
        // looking right (a wallclock value undermining FakeTimeProvider).
        var workItem = new WorkItem { TypeId = TypeId, StateId = "submitted" };
        Assert.Equal(default, workItem.SubmittedAt);
        Assert.Equal(default, workItem.LastModifiedAt);

        var note = new WorkItemNote { Text = "x" };
        Assert.Equal(default, note.CreatedAt);

        var entry = new WorkItemAuditEntry { Action = "x", ActionDisplayName = "X" };
        Assert.Equal(default, entry.CreatedAt);
    }
}
