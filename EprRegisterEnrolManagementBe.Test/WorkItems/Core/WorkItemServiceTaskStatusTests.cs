using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// Tests for <see cref="IWorkItemService.SetTaskStatusAsync"/> (epr-gl6):
/// generalises CompleteTask to the four-state lifecycle and dual-writes
/// <see cref="WorkItem.TaskStatusesByState"/> alongside the legacy
/// <see cref="WorkItem.CompletedTaskIdsByState"/> bucket.
/// </summary>
public class WorkItemServiceTaskStatusTests
{
    private const string TypeId = "test-type";
    private static readonly DateTime InitialNow = new(2026, 4, 27, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TickedNow = InitialNow.AddMinutes(5);

    private readonly IWorkItemPersistence _persistence = Substitute.For<IWorkItemPersistence>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(TickedNow));

    private WorkItemService BuildService(IWorkItemType type) =>
        new(new WorkItemRegistry([type]), _persistence, NullLogger<WorkItemService>.Instance, _time);

    private static TestWorkItemType BuildType() => new(
        TypeId,
        "Test type",
        initialState: new WorkItemState("submitted", "Submitted"),
        states: [
            new WorkItemState("submitted", "Submitted"),
            new WorkItemState("approved", "Approved", IsTerminal: true)
        ],
        tasksByState: new()
        {
            ["submitted"] = [new WorkItemTask("check-eligibility", "Check eligibility")]
        });

    private WorkItem ExistingWorkItem(
        Dictionary<string, HashSet<string>>? completed = null,
        Dictionary<string, Dictionary<string, WorkItemTaskStatus>>? statuses = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedAt = InitialNow,
            LastModifiedAt = InitialNow,
            SubmittedBy = "test-client",
            CompletedTaskIdsByState = completed ?? new(),
            TaskStatusesByState = statuses ?? new()
        };

    private static ClaimsPrincipal User() => new(new ClaimsIdentity(
        [new Claim("cognito:client_id", "test-client"), new Claim("user:id", "test-user")],
        "test"));

    private static ClaimsPrincipal UserWithoutActorId() =>
        new(new ClaimsIdentity([new Claim("cognito:client_id", "test-client")], "test"));

    [Fact]
    public async Task Sets_status_to_in_progress_and_writes_audit()
    {
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(BuildType()).SetTaskStatusAsync(
            workItem.Id, "check-eligibility", WorkItemTaskStatus.InProgress, User(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkItemTaskStatus.InProgress,
            workItem.TaskStatusesByState["submitted"]["check-eligibility"]);
        Assert.False(workItem.CompletedTaskIdsByState.TryGetValue("submitted", out var bucket)
            && bucket.Contains("check-eligibility"),
            "Non-Completed status must NOT be reflected in the legacy CompletedTaskIdsByState bucket.");
        Assert.Equal(TickedNow, workItem.LastModifiedAt);
        await _persistence.Received(1).ReplaceAsync(workItem, Arg.Any<CancellationToken>());

        var audit = Assert.Single(workItem.AuditLog);
        Assert.Equal("task-status-changed", audit.Action);
        Assert.Equal("check-eligibility", audit.Details["taskId"]);
        Assert.Equal("NotStarted", audit.Details["fromStatus"]);
        Assert.Equal("InProgress", audit.Details["toStatus"]);
    }

    [Fact]
    public async Task Setting_completed_via_set_status_dual_writes_legacy_bucket()
    {
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(BuildType()).SetTaskStatusAsync(
            workItem.Id, "check-eligibility", WorkItemTaskStatus.Completed, User(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkItemTaskStatus.Completed,
            workItem.TaskStatusesByState["submitted"]["check-eligibility"]);
        Assert.Contains("check-eligibility", workItem.CompletedTaskIdsByState["submitted"]);

        var audit = Assert.Single(workItem.AuditLog);
        Assert.Equal("task-status-changed", audit.Action);
        Assert.DoesNotContain(workItem.AuditLog, a => a.Action == "task-completed");
    }

    [Fact]
    public async Task Reverting_completed_to_not_started_removes_from_legacy_bucket()
    {
        var workItem = ExistingWorkItem(
            completed: new() { ["submitted"] = ["check-eligibility"] },
            statuses: new()
            {
                ["submitted"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["check-eligibility"] = WorkItemTaskStatus.Completed
                }
            });
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(BuildType()).SetTaskStatusAsync(
            workItem.Id, "check-eligibility", WorkItemTaskStatus.NotStarted, User(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkItemTaskStatus.NotStarted,
            workItem.TaskStatusesByState["submitted"]["check-eligibility"]);
        Assert.DoesNotContain("check-eligibility", workItem.CompletedTaskIdsByState["submitted"]);
    }

    [Theory]
    [InlineData(WorkItemTaskStatus.NotStarted)]
    [InlineData(WorkItemTaskStatus.InProgress)]
    [InlineData(WorkItemTaskStatus.Blocked)]
    [InlineData(WorkItemTaskStatus.Completed)]
    public async Task Idempotent_no_op_writes_neither_audit_nor_persistence(WorkItemTaskStatus current)
    {
        var statuses = new Dictionary<string, Dictionary<string, WorkItemTaskStatus>>(StringComparer.OrdinalIgnoreCase)
        {
            ["submitted"] = new(StringComparer.OrdinalIgnoreCase) { ["check-eligibility"] = current }
        };
        var completed = current == WorkItemTaskStatus.Completed
            ? new Dictionary<string, HashSet<string>> { ["submitted"] = ["check-eligibility"] }
            : new();
        var workItem = ExistingWorkItem(completed: completed, statuses: statuses);
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(BuildType()).SetTaskStatusAsync(
            workItem.Id, "check-eligibility", current, User(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Empty(workItem.AuditLog);
        Assert.Equal(InitialNow, workItem.LastModifiedAt);
        await _persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_task_id_is_rejected_and_no_write_occurs()
    {
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(BuildType()).SetTaskStatusAsync(
            workItem.Id, "unknown-task", WorkItemTaskStatus.InProgress, User(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.TaskNotApplicable, result.FailureCode);
        await _persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Missing_actor_user_id_is_rejected()
    {
        var workItem = ExistingWorkItem();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await BuildService(BuildType()).SetTaskStatusAsync(
            workItem.Id, "check-eligibility", WorkItemTaskStatus.InProgress, UserWithoutActorId(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.MissingActorIdentity, result.FailureCode);
        await _persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Work_item_not_found_returns_typed_failure()
    {
        _persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var result = await BuildService(BuildType()).SetTaskStatusAsync(
            Guid.NewGuid(), "check-eligibility", WorkItemTaskStatus.InProgress, User(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }
}
