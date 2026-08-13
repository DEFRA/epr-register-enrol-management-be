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
        //
        // RA-410: this fixture used to also re-inject two submitted-state
        // tasks, because a pre-v5 migration path auto-transitioned a
        // submitted item once its checklist was complete. That auto-transition
        // is gone (see ReAccreditationDulyMadeSnapshotMigration), so there is
        // nothing left to state a task checklist for.
        return new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v4",
            States = snapshot.States,
            Transitions = snapshot.Transitions
                .Where(t => t.ActionId != "duly-make")
                .Append(new WorkItemTransition(
                    "duly-make", "Mark as duly made", "submitted", "duly-made"))
                .ToList()
        };
    }

    private static WorkItem BuildItem(
        string stateId = "submitted",
        WorkItemTemplateSnapshot? snapshot = null) =>
        new()
        {
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            TemplateSnapshot = snapshot ?? BuildV4Snapshot(),
            TemplateVersion = "v4",
            SubmittedAt = DateTime.UtcNow,
            LastModifiedAt = DateTime.UtcNow
        };

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

    // RA-410: ApplyAsync_auto_transitions_submitted_item_when_all_tasks_complete
    // is gone along with the auto-transition it exercised — see
    // ReAccreditationDulyMadeSnapshotMigration's class doc. A submitted item
    // is now always left in submitted, regardless of task state (there is no
    // task state), and presented with the "Duly make" call to action like any
    // other. The two tests below now cover that unconditional behaviour.

    [Fact]
    public async Task ApplyAsync_leaves_a_submitted_item_in_submitted()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(stateId: "submitted");
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
        var item = BuildItem(stateId: "duly-made");
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

    // RA-410: ApplyAsync_stamps_audit_entry_with_injected_time and
    // ApplyAsync_sets_sla_clock_on_auto_transitioned_item are gone along with
    // the auto-transition whose side effects they asserted — see the class
    // doc on ReAccreditationDulyMadeSnapshotMigration. The migration never
    // writes an "action-applied" entry or starts an SLA clock any more; the
    // test below covers that.

    [Fact]
    public async Task ApplyAsync_does_not_set_an_sla_clock_on_a_submitted_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(stateId: "submitted");
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Null(item.SlaClock);
        Assert.DoesNotContain(item.AuditLog, e => e.Action == "sla-clock-started");
    }
}
