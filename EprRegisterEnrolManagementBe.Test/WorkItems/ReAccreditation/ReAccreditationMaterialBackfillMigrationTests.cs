using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

public class ReAccreditationMaterialBackfillMigrationTests
{
    private static WorkItem BuildItem(BsonDocument? payload = null) => new()
    {
        TypeId = ReAccreditationType.Id,
        StateId = "submitted",
        Payload = payload ?? new BsonDocument
        {
            ["materialsHandled"] = new BsonArray { "plastic", "glass" }
        }
    };

    private static WorkItemPage SinglePage(params WorkItem[] items) =>
        new(items, items.Length, 1, WorkItemQuery.MaxPageSize);

    private static ReAccreditationMaterialBackfillMigration BuildSut(TimeProvider? clock = null) =>
        new(NullLogger<ReAccreditationMaterialBackfillMigration>.Instance, clock);

    [Fact]
    public async Task ApplyAsync_copies_first_materialsHandled_entry_into_material()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Equal("plastic", item.Payload["material"].AsString);
    }

    [Fact]
    public async Task ApplyAsync_appends_material_backfilled_audit_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Contains(item.AuditLog, e =>
            e.Action == "material-backfilled" &&
            e.CreatedBy == "migration" &&
            e.Details["material"] == "plastic");
    }

    [Fact]
    public async Task ApplyAsync_skips_items_that_already_have_a_material()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(new BsonDocument
        {
            ["material"] = "paper",
            ["materialsHandled"] = new BsonArray { "plastic" }
        });
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
        Assert.Equal("paper", item.Payload["material"].AsString);
    }

    [Fact]
    public async Task ApplyAsync_skips_items_with_no_materialsHandled_array()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(new BsonDocument { ["organisationName"] = "Acme Ltd" });
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
        Assert.False(item.Payload.Contains("material"));
    }

    [Fact]
    public async Task ApplyAsync_skips_items_with_empty_materialsHandled_array()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(new BsonDocument { ["materialsHandled"] = new BsonArray() });
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }

    [Fact]
    public async Task ApplyAsync_uses_injected_time_for_audit_entry_created_at()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        var frozen = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        clock.SetUtcNow(frozen);

        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut(clock).ApplyAsync(persistence, ct);

        var entry = item.AuditLog.Single(e => e.Action == "material-backfilled");
        Assert.Equal(frozen.UtcDateTime, entry.CreatedAt);
    }

    [Fact]
    public async Task ApplyAsync_saves_once_per_item_needing_backfill()
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

        await BuildSut().ApplyAsync(persistence, ct);
    }

    [Fact]
    public async Task ApplyAsync_queries_all_states_including_archived()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct)
            .Returns(new WorkItemPage([], 0, 1, WorkItemQuery.MaxPageSize));

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.Received(1).QueryAsync(
            Arg.Is<WorkItemQuery>(q =>
                q.TypeIds != null &&
                q.TypeIds.Contains(ReAccreditationType.Id) &&
                q.StateIds == null &&
                q.IncludeArchived),
            ct);
    }
}
