using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

public class ReAccreditationDulyMadeSnapshotMigrationTests
{
    private static WorkItemTemplateSnapshot BuildV4Snapshot()
    {
        var type = new ReAccreditationType();
        var snapshot = WorkItemTemplateSnapshot.Capture(type);
        // Re-inject the duly-make transition to simulate a v4 snapshot.
        return new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v4",
            States = snapshot.States,
            Transitions = snapshot.Transitions
                .Append(new WorkItemTransition(
                    "duly-make", "Mark as duly made", "submitted", "duly-made"))
                .ToList(),
            TasksByState = snapshot.TasksByState
        };
    }

    private static WorkItem BuildItem(
        string stateId = "submitted",
        bool allTasksComplete = false,
        WorkItemTemplateSnapshot? snapshot = null)
    {
        snapshot ??= BuildV4Snapshot();
        var item = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            TemplateSnapshot = snapshot,
            TemplateVersion = "v4",
            SubmittedAt = DateTime.UtcNow,
            LastModifiedAt = DateTime.UtcNow
        };

        if (allTasksComplete)
        {
            foreach (var task in snapshot.GetTasksForState(stateId))
            {
                if (!item.TaskStatusesByState.TryGetValue(stateId, out var map))
                {
                    map = new Dictionary<string, WorkItemTaskStatus>(StringComparer.OrdinalIgnoreCase);
                    item.TaskStatusesByState[stateId] = map;
                }
                map[task.Id] = WorkItemTaskStatus.Completed;

                if (!item.CompletedTaskIdsByState.TryGetValue(stateId, out var bucket))
                {
                    bucket = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    item.CompletedTaskIdsByState[stateId] = bucket;
                }
                bucket.Add(task.Id);
            }
        }

        return item;
    }

    private static WorkItemPage SinglePage(params WorkItem[] items) =>
        new(items, items.Length, 1, WorkItemQuery.MaxPageSize);

    private static ReAccreditationDulyMadeSnapshotMigration BuildSut(TimeProvider? clock = null) =>
        new(NullLogger<ReAccreditationDulyMadeSnapshotMigration>.Instance, clock);

    [Fact]
    public async Task ApplyAsync_strips_duly_make_from_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.DoesNotContain(item.TemplateSnapshot!.Transitions, t => t.ActionId == "duly-make");
    }

    [Fact]
    public async Task ApplyAsync_bumps_template_version_to_v5()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Equal("v5", item.TemplateVersion);
        Assert.Equal("v5", item.TemplateSnapshot!.TemplateVersion);
    }

    [Fact]
    public async Task ApplyAsync_auto_transitions_submitted_item_when_all_tasks_complete()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(stateId: "submitted", allTasksComplete: true);
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Equal("duly-made", item.StateId);
        Assert.Contains(item.AuditLog, e =>
            e.Action == "action-applied" &&
            e.Details["actionId"] == "duly-make" &&
            e.Details["fromStateId"] == "submitted" &&
            e.Details["toStateId"] == "duly-made" &&
            e.CreatedBy == "migration");
    }

    [Fact]
    public async Task ApplyAsync_does_not_auto_transition_submitted_item_when_tasks_incomplete()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(stateId: "submitted", allTasksComplete: false);
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Equal("submitted", item.StateId);
        Assert.DoesNotContain(item.AuditLog, e => e.Action == "action-applied");
    }

    [Fact]
    public async Task ApplyAsync_does_not_auto_transition_non_submitted_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(stateId: "duly-made", allTasksComplete: true);
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Equal("duly-made", item.StateId);
        Assert.DoesNotContain(item.AuditLog, e => e.Action == "action-applied");
    }

    [Fact]
    public async Task ApplyAsync_skips_items_already_on_v5_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var v5Snapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        var item = BuildItem(snapshot: v5Snapshot);
        item.TemplateVersion = "v5";

        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().GetByIdAsync(default, ct);
        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task ApplyAsync_saves_once_per_item_needing_migration()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.Received(1).ReplaceAsync(item, ct);
    }

    [Fact]
    public async Task ApplyAsync_swallows_concurrency_exception_and_continues()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);
        persistence.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new WorkItemConcurrencyException(item.Id, expectedVersion: 0)));

        // Should not throw
        await BuildSut().ApplyAsync(persistence, ct);
    }

    [Fact]
    public async Task ApplyAsync_pages_through_all_results()
    {
        var ct = TestContext.Current.CancellationToken;
        var item1 = BuildItem();
        var item2 = BuildItem();
        const int pageSize = WorkItemQuery.MaxPageSize;

        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(
                Arg.Is<WorkItemQuery>(q => q.Page == 1), ct)
            .Returns(new WorkItemPage([item1], TotalCount: pageSize + 1, Page: 1, PageSize: pageSize));
        persistence.QueryAsync(
                Arg.Is<WorkItemQuery>(q => q.Page == 2), ct)
            .Returns(new WorkItemPage([item2], TotalCount: pageSize + 1, Page: 2, PageSize: pageSize));

        persistence.GetByIdAsync(item1.Id, ct).Returns(item1);
        persistence.GetByIdAsync(item2.Id, ct).Returns(item2);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.Received(1).ReplaceAsync(item1, ct);
        await persistence.Received(1).ReplaceAsync(item2, ct);
    }

    [Fact]
    public async Task ApplyAsync_stamps_audit_entry_with_injected_time()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        var frozen = new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);
        clock.SetUtcNow(frozen);

        var item = BuildItem(stateId: "submitted", allTasksComplete: true);
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut(clock).ApplyAsync(persistence, ct);

        var entry = item.AuditLog.Single(e => e.Action == "action-applied");
        Assert.Equal(frozen.UtcDateTime, entry.CreatedAt);
    }

    [Fact]
    public async Task ApplyAsync_sets_sla_clock_on_auto_transitioned_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        var frozen = new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);
        clock.SetUtcNow(frozen);

        var item = BuildItem(stateId: "submitted", allTasksComplete: true);
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut(clock).ApplyAsync(persistence, ct);

        Assert.NotNull(item.SlaClock);
        Assert.Equal(frozen.UtcDateTime, item.SlaClock!.StartedAt);
        Assert.Contains(item.AuditLog, e =>
            e.Action == "sla-clock-started" &&
            e.Details.ContainsKey("startedAt") &&
            e.Details.ContainsKey("targetDays") &&
            e.CreatedBy == "migration");
    }

    [Fact]
    public async Task ApplyAsync_does_not_set_sla_clock_for_non_auto_transitioned_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(stateId: "submitted", allTasksComplete: false);
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Null(item.SlaClock);
        Assert.DoesNotContain(item.AuditLog, e => e.Action == "sla-clock-started");
    }
}
