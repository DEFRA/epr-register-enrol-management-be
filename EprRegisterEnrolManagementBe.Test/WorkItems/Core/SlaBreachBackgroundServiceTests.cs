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

    private static WorkItem BreachedItem(
        TimeSpan? targetDuration = null, string stateId = "assessment-in-progress")
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
            StateId = stateId,
            SubmittedBy = "test-client",
            Payload = new BsonDocument(),
            SlaClock = clock
        };
    }

    // Clock started 10 days ago with the default 84-day target → 74 days remaining.
    private static WorkItem WithinDeadlineItem(string stateId = "assessment-in-progress") => new()
    {
        TypeId = "re-accreditation",
        StateId = stateId,
        SubmittedBy = "test-client",
        Payload = new BsonDocument(),
        SlaClock = new WorkItemSlaClock
        {
            StartedAt = s_fixedNow.AddDays(-10).UtcDateTime
        }
    };

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

        // Filter by the queried states, as the Mongo persistence does, so the
        // tests cover which states the job actually checks.
        var items = (pageItems ?? []).ToList();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.Arg<WorkItemQuery>();
                var matched = items
                    .Where(i => query.StateIds is not { Count: > 0 } || query.StateIds.Contains(i.StateId))
                    .ToList();
                return new WorkItemPage(matched, matched.Count, 1, matched.Count);
            });

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
        // epr-rr9s: the entry snapshots the state the item was in when the
        // deadline was breached, so the history row can render it.
        Assert.Equal(item.StateId, breachEntry.StateId);
        Assert.NotNull(breachEntry.StateId);
    }

    [Fact]
    public async Task RunOnceAsync_breaches_an_item_whose_deadline_was_moved_before_the_clock_start()
    {
        // RA-601 extreme case: the regulator moved the determination deadline
        // so far back that TargetDuration went NEGATIVE — the deadline now sits
        // before the clock ever started. The job must still pick it up on its
        // next pass, write one sla-breached entry, and not throw.
        var item = new WorkItem
        {
            TypeId = "re-accreditation",
            StateId = "assessment-in-progress",
            SubmittedBy = "test-client",
            Payload = new BsonDocument(),
            SlaClock = new WorkItemSlaClock
            {
                StartedAt = s_fixedNow.AddDays(-10).UtcDateTime,
                TargetDuration = TimeSpan.FromDays(-30)
            }
        };
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(item.SlaClock!.Breached);
        var breachEntry = Assert.Single(item.AuditLog, e => e.Action == "sla-breached");
        Assert.Equal("-30", breachEntry.Details["targetDays"]);
        Assert.Equal(s_fixedNow.AddDays(-40).UtcDateTime, item.SlaClock.DueAt);
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
    public async Task RunOnceAsync_repairs_the_breached_flag_when_the_audit_entry_already_exists()
    {
        // Idempotency-repair branch: the audit trail already records
        // sla-breached (e.g. a previous run wrote the entry but crashed
        // before persisting the flag flip) but SlaClock.Breached is still
        // false. The job must set the flag without writing a second entry.
        var item = BreachedItem();
        item.AuditLog.Add(new WorkItemAuditEntry
        {
            Action = "sla-breached",
            ActionDisplayName = "SLA breached",
            CreatedAt = s_fixedNow.UtcDateTime,
            CreatedBy = "system",
            Details = new Dictionary<string, string?>(),
        });
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(item.SlaClock!.Breached);
        Assert.Single(item.AuditLog, e => e.Action == "sla-breached");
        await sut.Persistence.Received(1).ReplaceAsync(item, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_skips_items_with_remaining_time()
    {
        var item = WithinDeadlineItem();
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(item.AuditLog);
        Assert.False(item.SlaClock!.Breached);
    }

    // ── RA-595: Duly made cases are checked as well as assessment ───────────

    [Fact]
    public async Task RunOnceAsync_breaches_an_overdue_duly_made_item()
    {
        var item = BreachedItem(stateId: "duly-made");
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(item.SlaClock!.Breached);
        var breachEntry = Assert.Single(item.AuditLog, e => e.Action == "sla-breached");
        Assert.Equal("duly-made", breachEntry.StateId);
        Assert.Equal("84", breachEntry.Details["targetDays"]);
        await sut.Persistence.Received(1).ReplaceAsync(item, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_skips_a_duly_made_item_within_its_deadline()
    {
        var item = WithinDeadlineItem("duly-made");
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(item.AuditLog);
        Assert.False(item.SlaClock!.Breached);
        await sut.Persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_breaches_duly_made_and_assessment_items_in_one_run()
    {
        var dulyMade = BreachedItem(stateId: "duly-made");
        var inAssessment = BreachedItem();
        var sut = Build([dulyMade, inAssessment]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(dulyMade.SlaClock!.Breached);
        Assert.True(inAssessment.SlaClock!.Breached);
        Assert.Equal("assessment-in-progress",
            Assert.Single(inAssessment.AuditLog, e => e.Action == "sla-breached").StateId);
    }

    [Theory]
    [InlineData("queried")]
    [InlineData("updated")]
    [InlineData("awaiting-decision")]
    [InlineData("submitted")]
    public async Task RunOnceAsync_does_not_check_other_states(string stateId)
    {
        var item = BreachedItem(stateId: stateId);
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(item.SlaClock!.Breached);
        Assert.Empty(item.AuditLog);
    }

    // ── RA-527 follow-up: a started case (still "Duly made") is checked ─────

    [Fact]
    public async Task RunOnceAsync_breaches_an_overdue_started_item()
    {
        var item = BreachedItem(stateId: "assessment-started");
        var sut = Build([item]);

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(item.SlaClock!.Breached);
        var breachEntry = Assert.Single(item.AuditLog, e => e.Action == "sla-breached");
        Assert.Equal("assessment-started", breachEntry.StateId);
        await sut.Persistence.Received(1).ReplaceAsync(item, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_queries_only_duly_made_started_and_assessment_states()
    {
        var sut = Build();

        await sut.Service.RunOnceAsync(TestContext.Current.CancellationToken);

        await sut.Persistence.Received(1).QueryAsync(
            Arg.Is<WorkItemQuery>(q =>
                q.StateIds != null
                && q.StateIds.Count == 3
                && q.StateIds.Contains("duly-made")
                && q.StateIds.Contains("assessment-started")
                && q.StateIds.Contains("assessment-in-progress")),
            Arg.Any<CancellationToken>());
    }
}
