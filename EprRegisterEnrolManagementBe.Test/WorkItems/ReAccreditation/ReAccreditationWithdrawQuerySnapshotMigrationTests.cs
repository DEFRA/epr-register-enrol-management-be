using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-252: adds the <c>withdraw-during-query</c> transition to every
/// re-accreditation work item's frozen snapshot (v8 → v9). Mirrors
/// <see cref="ReAccreditationResumeSnapshotMigrationTests"/>'s structure.
/// </summary>
public class ReAccreditationWithdrawQuerySnapshotMigrationTests
{
    private static WorkItemTemplateSnapshot BuildV8Snapshot()
    {
        var type = new ReAccreditationType();
        var snapshot = WorkItemTemplateSnapshot.Capture(type);
        // Strip withdraw-during-query (present on the live v9 type) to
        // simulate a pre-migration v8 snapshot.
        return new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v8",
            States = snapshot.States,
            Transitions = snapshot
                .Transitions.Where(t => t.ActionId != "withdraw-during-query")
                .ToList(),
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
            TemplateSnapshot = snapshot ?? BuildV8Snapshot(),
            TemplateVersion = "v8",
            SubmittedAt = DateTime.UtcNow,
            LastModifiedAt = DateTime.UtcNow,
        };

    private static WorkItemPage SinglePage(params WorkItem[] items) =>
        new(items, items.Length, 1, WorkItemQuery.MaxPageSize);

    private static ReAccreditationWithdrawQuerySnapshotMigration BuildSut() =>
        new(NullLogger<ReAccreditationWithdrawQuerySnapshotMigration>.Instance);

    [Fact]
    public async Task ApplyAsync_adds_the_withdraw_during_query_transition_to_the_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Contains(
            item.TemplateSnapshot!.Transitions,
            t => t.ActionId == "withdraw-during-query"
        );
    }

    [Fact]
    public async Task ApplyAsync_preserves_existing_transitions()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var originalCount = item.TemplateSnapshot!.Transitions.Count;
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Contains(item.TemplateSnapshot!.Transitions, t => t.ActionId == "withdraw");
        Assert.Equal(originalCount + 1, item.TemplateSnapshot!.Transitions.Count);
    }

    [Fact]
    public async Task ApplyAsync_bumps_template_version_to_v9()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Equal("v9", item.TemplateVersion);
        Assert.Equal("v9", item.TemplateSnapshot!.TemplateVersion);
    }

    [Fact]
    public async Task ApplyAsync_skips_items_already_on_v9_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var v9Snapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        var item = BuildItem(snapshot: v9Snapshot);
        item.TemplateVersion = "v9";

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
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);
        persistence
            .ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException(new WorkItemConcurrencyException(item.Id, expectedVersion: 0))
            );

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
