using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// Unit tests for <see cref="SlaBreachBackgroundService.RunOnceAsync"/>.
/// Persistence and audit appender are substituted; <see cref="FakeTimeProvider"/>
/// controls "now".
/// </summary>
public class SlaBreachBackgroundServiceTests
{
    private static readonly DateTimeOffset s_fixedNow =
        new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static WorkItem BreachedItem(TimeSpan? targetDuration = null)
    {
        var target = targetDuration ?? TimeSpan.FromDays(84);
        var clock = new WorkItemSlaClock
        {
            // Start one day past the deadline so Remaining(now) < 0 for any target.
            StartedAt = s_fixedNow.Add(-(target + TimeSpan.FromDays(1))).UtcDateTime
        };
        if (targetDuration.HasValue)
        {
            clock.TargetDuration = targetDuration.Value;
        }
        return new WorkItem
        {
            TypeId = "re-accreditation",
            StateId = "assessment-in-progress",
            SubmittedBy = "test-client",
            Payload = new BsonDocument(),
            SlaClock = clock
        };
    }

    private sealed record Sut(
        SlaBreachBackgroundService Service,
        IWorkItemPersistence Persistence,
        IWorkItemAuditAppender AuditAppender);

    private static Sut Build(IEnumerable<WorkItem>? pageItems = null, DateTimeOffset? now = null)
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        var time = new FakeTimeProvider(now ?? s_fixedNow);
        var config = new ConfigurationBuilder().Build();

        var items = (pageItems ?? []).ToList();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(items, items.Count, 1, items.Count));

        foreach (var item in items)
        {
            persistence.GetByIdAsync(item.Id, Arg.Any<CancellationToken>())
                .Returns(item);
        }

        // Wire up a real service provider so the hosted service can resolve
        // persistence and audit appender from a scope.
        var services = new ServiceCollection();
        services.AddSingleton(persistence);
        services.AddSingleton(auditAppender);
        var provider = services.BuildServiceProvider();

        var service = new SlaBreachBackgroundService(
            provider, time,
            NullLogger<SlaBreachBackgroundService>.Instance,
            config);

        return new Sut(service, persistence, auditAppender);
    }

    // ── targetDays reflects actual TargetDuration, not a hardcoded value ───

    [Fact]
    public async Task RunOnceAsync_breach_audit_targetDays_matches_actual_clock_duration()
    {
        var extendedTarget = TimeSpan.FromDays(98); // team leader extended by 14 days
        var item = BreachedItem(extendedTarget);
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        var breachEntry = item.AuditLog.FirstOrDefault(e => e.Action == "sla-breached");
        Assert.NotNull(breachEntry);
        Assert.Equal("98", breachEntry!.Details["targetDays"]);
    }

    [Fact]
    public async Task RunOnceAsync_breach_audit_targetDays_is_84_for_default_clock()
    {
        var item = BreachedItem(); // default 84-day target
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        var breachEntry = item.AuditLog.FirstOrDefault(e => e.Action == "sla-breached");
        Assert.NotNull(breachEntry);
        Assert.Equal("84", breachEntry!.Details["targetDays"]);
    }

    [Fact]
    public async Task RunOnceAsync_skips_items_with_no_sla_clock()
    {
        var item = new WorkItem
        {
            TypeId = "re-accreditation",
            StateId = "assessment-in-progress",
            SubmittedBy = "test-client",
            Payload = new BsonDocument(),
            SlaClock = null
        };
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(item.AuditLog);
    }

    [Fact]
    public async Task RunOnceAsync_skips_items_already_marked_breached()
    {
        var item = BreachedItem();
        item.SlaClock!.Breached = true;
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        // No new sla-breached entry because Breached was already true.
        Assert.Empty(item.AuditLog);
    }

    [Fact]
    public async Task RunOnceAsync_skips_items_with_remaining_time()
    {
        // Clock started 10 days ago with 84-day target → 74 days remaining.
        var item = new WorkItem
        {
            TypeId = "re-accreditation",
            StateId = "assessment-in-progress",
            SubmittedBy = "test-client",
            Payload = new BsonDocument(),
            SlaClock = new WorkItemSlaClock
            {
                StartedAt = s_fixedNow.AddDays(-10).UtcDateTime
            }
        };
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(item.AuditLog);
        Assert.False(item.SlaClock!.Breached);
    }
}
