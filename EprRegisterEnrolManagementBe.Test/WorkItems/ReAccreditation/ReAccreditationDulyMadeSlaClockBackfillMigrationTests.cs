using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

public class ReAccreditationDulyMadeSlaClockBackfillMigrationTests
{
    private static WorkItem BuildDulyMadeItem(WorkItemSlaClock? slaClock = null)
    {
        var lastModified = new DateTime(2026, 5, 10, 9, 0, 0, DateTimeKind.Utc);
        return new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "duly-made",
            SlaClock = slaClock,
            SubmittedAt = lastModified.AddDays(-2),
            LastModifiedAt = lastModified
        };
    }

    private static WorkItemPage SinglePage(params WorkItem[] items) =>
        new(items, items.Length, 1, WorkItemQuery.MaxPageSize);

    private static ReAccreditationDulyMadeSlaClockBackfillMigration BuildSut(TimeProvider? clock = null) =>
        new(NullLogger<ReAccreditationDulyMadeSlaClockBackfillMigration>.Instance, clock);

    [Fact]
    public async Task ApplyAsync_sets_sla_clock_startedAt_to_last_modified_at()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildDulyMadeItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.NotNull(item.SlaClock);
        Assert.Equal(item.LastModifiedAt, item.SlaClock!.StartedAt);
    }

    [Fact]
    public async Task ApplyAsync_appends_sla_clock_started_audit_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildDulyMadeItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Contains(item.AuditLog, e =>
            e.Action == "sla-clock-started" &&
            e.CreatedBy == "migration" &&
            e.Details.ContainsKey("startedAt") &&
            e.Details.ContainsKey("targetDays"));
    }

    [Fact]
    public async Task ApplyAsync_skips_items_that_already_have_a_sla_clock()
    {
        var ct = TestContext.Current.CancellationToken;
        var existingClock = new WorkItemSlaClock { StartedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var item = BuildDulyMadeItem(slaClock: existingClock);
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
        Assert.Equal(existingClock.StartedAt, item.SlaClock!.StartedAt);
    }

    [Fact]
    public async Task ApplyAsync_uses_injected_time_for_audit_entry_created_at()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        var frozen = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        clock.SetUtcNow(frozen);

        var item = BuildDulyMadeItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut(clock).ApplyAsync(persistence, ct);

        var entry = item.AuditLog.Single(e => e.Action == "sla-clock-started");
        Assert.Equal(frozen.UtcDateTime, entry.CreatedAt);
    }

    [Fact]
    public async Task ApplyAsync_saves_once_per_item_needing_backfill()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildDulyMadeItem();
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
        var item = BuildDulyMadeItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);
        persistence.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new WorkItemConcurrencyException(item.Id, expectedVersion: 0)));

        await BuildSut().ApplyAsync(persistence, ct);
    }

    [Fact]
    public async Task ApplyAsync_queries_only_duly_made_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct)
            .Returns(new WorkItemPage([], 0, 1, WorkItemQuery.MaxPageSize));

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.Received(1).QueryAsync(
            Arg.Is<WorkItemQuery>(q =>
                q.StateIds != null &&
                q.StateIds.Contains("duly-made") &&
                q.TypeIds != null &&
                q.TypeIds.Contains(ReAccreditationType.Id)),
            ct);
    }
}
