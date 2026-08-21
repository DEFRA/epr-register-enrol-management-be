using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-351: adds the <c>queried → queried</c> <c>sla-extend</c> transition to
/// every re-accreditation work item's frozen snapshot (v12 → v13). Mirrors
/// <see cref="ReAccreditationWithdrawQuerySnapshotMigrationTests"/>'s
/// structure. The distinguishing subtlety: a v12 snapshot already carries an
/// <c>sla-extend</c> transition (the assessment-in-progress self-loop), so the
/// migration keys off the from-state, not just the action id.
/// </summary>
public class ReAccreditationSlaExtendQuerySnapshotMigrationTests
{
    private static bool IsQueriedSlaExtend(WorkItemTransition t) =>
        t.ActionId == "sla-extend" && t.FromStateId == "queried";

    private static WorkItemTemplateSnapshot BuildV12Snapshot()
    {
        var type = new ReAccreditationType();
        var snapshot = WorkItemTemplateSnapshot.Capture(type);
        // Strip the queried sla-extend self-loop (present on the live v13 type)
        // to simulate a pre-migration v12 snapshot, but keep the
        // assessment-in-progress sla-extend that a v12 snapshot already has.
        return new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v12",
            States = snapshot.States,
            Transitions = snapshot.Transitions.Where(t => !IsQueriedSlaExtend(t)).ToList(),
        };
    }

    private static WorkItem BuildItem(
        string stateId = "queried",
        WorkItemTemplateSnapshot? snapshot = null
    ) =>
        new()
        {
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            TemplateSnapshot = snapshot ?? BuildV12Snapshot(),
            TemplateVersion = "v12",
            SubmittedAt = DateTime.UtcNow,
            LastModifiedAt = DateTime.UtcNow,
        };

    private static WorkItemPage SinglePage(params WorkItem[] items) =>
        new(items, items.Length, 1, WorkItemQuery.MaxPageSize);

    private static ReAccreditationSlaExtendQuerySnapshotMigration BuildSut() =>
        new(NullLogger<ReAccreditationSlaExtendQuerySnapshotMigration>.Instance);

    [Fact]
    public async Task ApplyAsync_skips_an_item_with_no_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        item.TemplateSnapshot = null;
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task ApplyAsync_skips_an_item_whose_full_document_has_disappeared_by_the_time_it_is_refetched()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns((WorkItem?)null);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task ApplyAsync_adds_the_queried_sla_extend_transition_to_the_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Contains(item.TemplateSnapshot!.Transitions, IsQueriedSlaExtend);
    }

    [Fact]
    public async Task ApplyAsync_preserves_existing_transitions_including_assessment_sla_extend()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var originalCount = item.TemplateSnapshot!.Transitions.Count;
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        // The pre-existing assessment-in-progress sla-extend must survive.
        Assert.Contains(
            item.TemplateSnapshot!.Transitions,
            t => t.ActionId == "sla-extend" && t.FromStateId == "assessment-in-progress"
        );
        Assert.Contains(item.TemplateSnapshot!.Transitions, t => t.ActionId == "withdraw");
        Assert.Equal(originalCount + 1, item.TemplateSnapshot!.Transitions.Count);
    }

    [Fact]
    public async Task ApplyAsync_bumps_template_version_to_v13()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Equal("v13", item.TemplateVersion);
        Assert.Equal("v13", item.TemplateSnapshot!.TemplateVersion);
    }

    [Fact]
    public async Task ApplyAsync_skips_items_already_on_v13_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var v13Snapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        var item = BuildItem(snapshot: v13Snapshot);
        item.TemplateVersion = "v13";

        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().GetByIdAsync(default, ct);
        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task ApplyAsync_does_not_change_the_work_items_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(stateId: "queried");
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Equal("queried", item.StateId);
        Assert.Empty(item.AuditLog);
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
        // Two items need migrating; the first loses a concurrency race on save.
        // The exception must be swallowed AND the second item still migrated —
        // one item losing the race must not abort the whole migration.
        var conflicted = BuildItem();
        var succeeding = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence
            .QueryAsync(Arg.Any<WorkItemQuery>(), ct)
            .Returns(SinglePage(conflicted, succeeding));
        persistence.GetByIdAsync(conflicted.Id, ct).Returns(conflicted);
        persistence.GetByIdAsync(succeeding.Id, ct).Returns(succeeding);
        persistence
            .ReplaceAsync(conflicted, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException(
                    new WorkItemConcurrencyException(conflicted.Id, expectedVersion: 0)
                )
            );

        await BuildSut().ApplyAsync(persistence, ct);

        // The concurrency loss is swallowed (no throw) and the next item is
        // still migrated and saved.
        await persistence.Received(1).ReplaceAsync(succeeding, ct);
        Assert.Contains(succeeding.TemplateSnapshot!.Transitions, IsQueriedSlaExtend);
    }

    [Fact]
    public async Task ApplyAsync_pages_through_all_results()
    {
        var ct = TestContext.Current.CancellationToken;
        var item1 = BuildItem();
        var item2 = BuildItem();
        const int pageSize = WorkItemQuery.MaxPageSize;

        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence
            .QueryAsync(Arg.Is<WorkItemQuery>(q => q.Page == 1), ct)
            .Returns(
                new WorkItemPage([item1], TotalCount: pageSize + 1, Page: 1, PageSize: pageSize)
            );
        persistence
            .QueryAsync(Arg.Is<WorkItemQuery>(q => q.Page == 2), ct)
            .Returns(
                new WorkItemPage([item2], TotalCount: pageSize + 1, Page: 2, PageSize: pageSize)
            );

        persistence.GetByIdAsync(item1.Id, ct).Returns(item1);
        persistence.GetByIdAsync(item2.Id, ct).Returns(item2);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.Received(1).ReplaceAsync(item1, ct);
        await persistence.Received(1).ReplaceAsync(item2, ct);
    }
}
