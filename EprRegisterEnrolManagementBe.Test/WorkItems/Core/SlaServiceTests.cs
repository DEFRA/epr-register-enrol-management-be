using System.Security.Claims;
using System.Xml;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-131: unit tests for <see cref="SlaService"/>. Uses NSubstitute
/// mocks for <see cref="IWorkItemPersistence"/> — no Mongo needed.
/// </summary>
public class SlaServiceTests
{
    private static readonly DateTime UtcNow = new(2026, 5, 19, 12, 0, 0, DateTimeKind.Utc);

    private readonly IWorkItemPersistence _persistence = Substitute.For<IWorkItemPersistence>();
    private readonly IWorkItemPostActionHook _hook = Substitute.For<IWorkItemPostActionHook>();
    private readonly FakeTimeProvider _time = new(UtcNow);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private SlaService BuildService() =>
        new(
            _persistence,
            NullLogger<SlaService>.Instance,
            _time,
            [_hook]);

    private static ClaimsPrincipal TeamLeader(string userId = "tl-1") =>
        new(new ClaimsIdentity(
        [
            new Claim("client_id", "test-client"),
            new Claim("user:id", userId),
            new Claim("user:name", "Test Leader"),
            new Claim(ClaimTypes.Role, "standard")
        ], "test"));

    private static ClaimsPrincipal NoIdentityUser() =>
        new(new ClaimsIdentity(
        [
            new Claim("client_id", "test-client")
            // No user:id claim
        ], "test"));

    private static WorkItem WorkItemWithClock(
        Guid? id = null,
        TimeSpan? targetDuration = null,
        DateTime? startedAt = null,
        bool breached = false) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            TypeId = "test",
            StateId = "assessment-in-progress",
            SubmittedAt = UtcNow.AddDays(-10),
            LastModifiedAt = UtcNow.AddDays(-10),
            SubmittedBy = "test-client",
            SlaClock = new WorkItemSlaClock
            {
                StartedAt = startedAt ?? UtcNow.AddDays(-10),
                TargetDuration = targetDuration ?? TimeSpan.FromDays(84),
                Breached = breached
            }
        };

    private static WorkItem WorkItemWithoutClock(Guid? id = null) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            TypeId = "test",
            StateId = "submitted",
            SubmittedAt = UtcNow.AddDays(-1),
            LastModifiedAt = UtcNow.AddDays(-1),
            SubmittedBy = "test-client",
            SlaClock = null
        };

    private sealed class FakeTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    // ── Constructor defaults ─────────────────────────────────────────────────

    [Fact]
    public async Task Constructor_defaults_time_provider_and_hooks_when_omitted()
    {
        // Covers the `timeProvider ?? TimeProvider.System` and
        // `postActionHooks?.ToArray() ?? Array.Empty<...>()` branches, which
        // BuildService() above never exercises because it always supplies
        // both explicitly.
        var service = new SlaService(_persistence, NullLogger<SlaService>.Instance);

        var workItem = WorkItemWithClock();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await service.ExtendAsync(
            workItem.Id, TimeSpan.FromDays(7), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        // LastModifiedAt should be close to real wall-clock time, proving
        // TimeProvider.System (not a fixed fake) was used.
        Assert.True(
            Math.Abs((DateTime.UtcNow - result.WorkItem!.LastModifiedAt).TotalMinutes) < 5);
    }

    // ── ExtendAsync — success path ────────────────────────────────────────────

    [Fact]
    public async Task ExtendAsync_adds_additional_duration_to_target_duration()
    {
        var workItem = WorkItemWithClock(targetDuration: TimeSpan.FromDays(84));
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(14), "Extra time needed",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.FromDays(98), result.WorkItem!.SlaClock!.TargetDuration);
    }

    [Fact]
    public async Task ExtendAsync_persists_the_updated_work_item()
    {
        var workItem = WorkItemWithClock();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(7), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        await _persistence.Received(1).ReplaceAsync(workItem, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtendAsync_updates_last_modified_at_to_now()
    {
        var workItem = WorkItemWithClock();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(7), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.Equal(UtcNow, result.WorkItem!.LastModifiedAt);
    }

    [Fact]
    public async Task ExtendAsync_writes_sla_extended_audit_entry()
    {
        var workItem = WorkItemWithClock(
            targetDuration: TimeSpan.FromDays(84),
            startedAt: UtcNow.AddDays(-10));
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(14), "Needs more time",
            TeamLeader("tl-alice"), TestContext.Current.CancellationToken);

        var entry = Assert.Single(result.WorkItem!.AuditLog);
        Assert.Equal("sla-extended", entry.Action);
        Assert.Equal("Determination deadline extended", entry.ActionDisplayName);
        Assert.Equal("tl-alice", entry.CreatedBy);
        Assert.Equal(UtcNow, entry.CreatedAt);
        Assert.Equal("Needs more time", entry.Details["reason"]);
        Assert.Equal("tl-alice", entry.Details["actorUserId"]);
        // epr-rr9s: the entry snapshots the state as of this event. Extending
        // the deadline does not move the item, so it is the state it was in.
        Assert.Equal(result.WorkItem!.StateId, entry.StateId);
        Assert.NotNull(entry.StateId);
        // Before snapshot
        Assert.Equal(
            XmlConvert.ToString(TimeSpan.FromDays(84)),
            entry.Details["beforeTargetDuration"]);
        // After snapshot (84 + 14 = 98)
        Assert.Equal(
            XmlConvert.ToString(TimeSpan.FromDays(98)),
            entry.Details["afterTargetDuration"]);
        // additionalDuration extra field
        Assert.Equal(
            XmlConvert.ToString(TimeSpan.FromDays(14)),
            entry.Details["additionalDuration"]);
    }

    [Fact]
    public async Task ExtendAsync_invokes_post_action_hook_with_extend_action_id()
    {
        var workItem = WorkItemWithClock();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(7), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        await _hook.Received(1).OnActionAppliedAsync(
            workItem,
            SlaService.ExtendActionId,
            workItem.StateId,
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtendAsync_swallows_hook_exception_and_still_returns_success()
    {
        var workItem = WorkItemWithClock();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);
        _hook.OnActionAppliedAsync(
                Arg.Any<WorkItem>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Notify unavailable"));

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(7), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    // ── ExtendAsync — validation failures ────────────────────────────────────

    [Fact]
    public async Task ExtendAsync_returns_missing_actor_identity_when_no_user_id()
    {
        var result = await BuildService().ExtendAsync(
            Guid.NewGuid(), TimeSpan.FromDays(7), "reason",
            NoIdentityUser(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(SlaActionFailureCode.MissingActorIdentity, result.FailureCode);
    }

    [Fact]
    public async Task ExtendAsync_returns_invalid_request_when_reason_empty()
    {
        var result = await BuildService().ExtendAsync(
            Guid.NewGuid(), TimeSpan.FromDays(7), "   ",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.Equal(SlaActionFailureCode.InvalidRequest, result.FailureCode);
        Assert.Contains("reason", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtendAsync_returns_invalid_request_for_zero_duration()
    {
        // RA-601: zero remains rejected — re-submitting the current deadline
        // is a no-op. The frontend rejects "same date" for the same reason.
        var result = await BuildService().ExtendAsync(
            Guid.NewGuid(), TimeSpan.Zero, "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.Equal(SlaActionFailureCode.InvalidRequest, result.FailureCode);
        Assert.Contains("non-zero", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    // ── RA-601: moving the determination deadline EARLIER ────────────────────

    [Fact]
    public async Task ExtendAsync_accepts_a_negative_duration_and_moves_the_deadline_earlier()
    {
        var workItem = WorkItemWithClock(targetDuration: TimeSpan.FromDays(84));
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(-14), "Regulator brought the determination forward",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.FromDays(70), result.WorkItem!.SlaClock!.TargetDuration);
        await _persistence.Received(1).ReplaceAsync(workItem, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtendAsync_writes_the_audit_entry_for_a_reduction_with_a_negative_iso_duration()
    {
        var startedAt = UtcNow.AddDays(-10);
        var workItem = WorkItemWithClock(
            targetDuration: TimeSpan.FromDays(84), startedAt: startedAt);
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(-14), "Brought forward",
            TeamLeader(), TestContext.Current.CancellationToken);

        var entry = Assert.Single(result.WorkItem!.AuditLog);
        // Action id and audit action value are unchanged by RA-601.
        Assert.Equal("sla-extended", entry.Action);
        Assert.Equal("Brought forward", entry.Details["reason"]);
        Assert.Equal("-P14D", entry.Details["additionalDuration"]);
        Assert.Equal(XmlConvert.ToString(TimeSpan.FromDays(84)), entry.Details["beforeTargetDuration"]);
        Assert.Equal(XmlConvert.ToString(TimeSpan.FromDays(70)), entry.Details["afterTargetDuration"]);
        Assert.Equal("tl-1", entry.CreatedBy);
    }

    [Fact]
    public async Task ExtendAsync_accepts_a_deadline_earlier_than_today()
    {
        // The clock started 10 days ago with an 84-day target. Pulling 80 days
        // off puts the deadline 6 days in the PAST: valid, and the item is
        // immediately breach-eligible.
        var workItem = WorkItemWithClock(targetDuration: TimeSpan.FromDays(84));
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(-80), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var clock = result.WorkItem!.SlaClock!;
        Assert.True(clock.DueAt < UtcNow);
        Assert.True(clock.Remaining(UtcNow) < TimeSpan.Zero);
        Assert.Equal(WorkItemSlaState.Breached, clock.ComputeState(UtcNow));
    }

    [Theory]
    [InlineData(-84)]  // TargetDuration lands exactly on zero
    [InlineData(-200)] // TargetDuration goes negative — deadline before StartedAt
    public async Task ExtendAsync_accepts_a_deadline_at_or_before_the_clock_start(int days)
    {
        var startedAt = UtcNow.AddDays(-10);
        var workItem = WorkItemWithClock(
            targetDuration: TimeSpan.FromDays(84), startedAt: startedAt);
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(days), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var clock = result.WorkItem!.SlaClock!;
        Assert.Equal(TimeSpan.FromDays(84 + days), clock.TargetDuration);
        // Nothing throws: the deadline simply sits at or before the start.
        Assert.Equal(startedAt + TimeSpan.FromDays(84 + days), clock.DueAt);
        Assert.Equal(WorkItemSlaState.Breached, clock.ComputeState(UtcNow));
    }

    [Theory]
    [InlineData(-4_000_000)] // deadline underflows DateTime.MinValue
    [InlineData(4_000_000)]  // deadline overflows DateTime.MaxValue
    public async Task ExtendAsync_rejects_a_deadline_outside_the_representable_date_range(int days)
    {
        // Not the "floor" RA-601 declined — this is the edge of what a
        // DateTime can hold. Nothing throws; the caller gets a 422-mapped
        // InvalidRequest and the work item is left untouched.
        var workItem = WorkItemWithClock();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(days), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.Equal(SlaActionFailureCode.InvalidRequest, result.FailureCode);
        Assert.Contains("representable", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TimeSpan.FromDays(84), workItem.SlaClock!.TargetDuration);
        Assert.Empty(workItem.AuditLog);
        await _persistence.DidNotReceive().ReplaceAsync(
            Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtendAsync_rejects_a_duration_that_would_overflow_the_timespan_itself()
    {
        var workItem = WorkItemWithClock(targetDuration: TimeSpan.FromTicks(long.MaxValue - 10));
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromTicks(1_000), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.Equal(SlaActionFailureCode.InvalidRequest, result.FailureCode);
    }

    [Fact]
    public async Task ExtendAsync_allows_any_duration_with_no_upper_cap()
    {
        // RA-447/CM6: MaxExtensionDays has been removed entirely — a
        // caseworker may extend by any positive duration.
        var workItem = WorkItemWithClock();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(365), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ExtendAsync_returns_not_found_when_work_item_missing()
    {
        var id = Guid.NewGuid();
        _persistence.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);

        var result = await BuildService().ExtendAsync(
            id, TimeSpan.FromDays(7), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.Equal(SlaActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task ExtendAsync_returns_clock_not_started_when_sla_clock_null()
    {
        var workItem = WorkItemWithoutClock();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(7), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.Equal(SlaActionFailureCode.ClockNotStarted, result.FailureCode);
    }

    [Fact]
    public async Task ExtendAsync_returns_concurrency_conflict_on_replace_exception()
    {
        var workItem = WorkItemWithClock();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);
        _persistence.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new WorkItemConcurrencyException(workItem.Id, 0));

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(7), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.Equal(SlaActionFailureCode.ConcurrencyConflict, result.FailureCode);
    }

    // ── OverrideAsync — success path ──────────────────────────────────────────

    [Fact]
    public async Task OverrideAsync_replaces_target_duration()
    {
        var workItem = WorkItemWithClock(targetDuration: TimeSpan.FromDays(84));
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().OverrideAsync(
            workItem.Id, TimeSpan.FromDays(60), null, "Regulator decision",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.FromDays(60), result.WorkItem!.SlaClock!.TargetDuration);
    }

    [Fact]
    public async Task OverrideAsync_replaces_started_at_when_provided()
    {
        var originalStart = UtcNow.AddDays(-20);
        var newStart = UtcNow.AddDays(-30);
        var workItem = WorkItemWithClock(startedAt: originalStart);
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().OverrideAsync(
            workItem.Id, TimeSpan.FromDays(84), newStart, "Rebase clock",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.Equal(newStart, result.WorkItem!.SlaClock!.StartedAt);
    }

    [Fact]
    public async Task OverrideAsync_defaults_started_at_to_now_when_not_provided()
    {
        var originalStart = UtcNow.AddDays(-10);
        var workItem = WorkItemWithClock(startedAt: originalStart);
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().OverrideAsync(
            workItem.Id, TimeSpan.FromDays(84), null, "Just change target",
            TeamLeader(), TestContext.Current.CancellationToken);

        // BA confirmed (RA-131): omitting newStartedAt should default to today.
        Assert.Equal(UtcNow, result.WorkItem!.SlaClock!.StartedAt);
    }

    [Fact]
    public async Task OverrideAsync_normalises_started_at_to_utc()
    {
        var workItem = WorkItemWithClock();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);
        // Pass a local-kind datetime (e.g. from a non-UTC consumer)
        var localTime = DateTime.SpecifyKind(UtcNow.AddDays(-5), DateTimeKind.Local);

        var result = await BuildService().OverrideAsync(
            workItem.Id, TimeSpan.FromDays(84), localTime, "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(DateTimeKind.Utc, result.WorkItem!.SlaClock!.StartedAt.Kind);
    }

    [Fact]
    public async Task OverrideAsync_preserves_an_already_utc_started_at_without_converting()
    {
        var workItem = WorkItemWithClock();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);
        var utcTime = DateTime.SpecifyKind(UtcNow.AddDays(-5), DateTimeKind.Utc);

        var result = await BuildService().OverrideAsync(
            workItem.Id, TimeSpan.FromDays(84), utcTime, "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(utcTime, result.WorkItem!.SlaClock!.StartedAt);
        Assert.Equal(DateTimeKind.Utc, result.WorkItem.SlaClock.StartedAt.Kind);
    }

    [Fact]
    public async Task OverrideAsync_accepts_a_started_at_of_exactly_now()
    {
        var workItem = WorkItemWithClock();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().OverrideAsync(
            workItem.Id, TimeSpan.FromDays(84), UtcNow, "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task OverrideAsync_writes_sla_overridden_audit_entry()
    {
        var workItem = WorkItemWithClock(
            targetDuration: TimeSpan.FromDays(84),
            startedAt: UtcNow.AddDays(-10));
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().OverrideAsync(
            workItem.Id, TimeSpan.FromDays(60), null, "Regulatory override",
            TeamLeader("tl-bob"), TestContext.Current.CancellationToken);

        var entry = Assert.Single(result.WorkItem!.AuditLog);
        Assert.Equal("sla-overridden", entry.Action);
        Assert.Equal("SLA overridden", entry.ActionDisplayName);
        Assert.Equal("tl-bob", entry.CreatedBy);
        Assert.Equal(UtcNow, entry.CreatedAt);
        Assert.Equal("Regulatory override", entry.Details["reason"]);
        Assert.Equal(
            XmlConvert.ToString(TimeSpan.FromDays(84)),
            entry.Details["beforeTargetDuration"]);
        Assert.Equal(
            XmlConvert.ToString(TimeSpan.FromDays(60)),
            entry.Details["afterTargetDuration"]);
    }

    [Fact]
    public async Task OverrideAsync_does_not_invoke_post_action_hook()
    {
        var workItem = WorkItemWithClock();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        await BuildService().OverrideAsync(
            workItem.Id, TimeSpan.FromDays(60), null, "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        await _hook.DidNotReceiveWithAnyArgs().OnActionAppliedAsync(
            default!, default!, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OverrideAsync_allows_any_duration_increase()
    {
        var workItem = WorkItemWithClock(targetDuration: TimeSpan.FromDays(14));
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        // BA confirmed (RA-131): no cap on override — regulators agree offline.
        var result = await BuildService().OverrideAsync(
            workItem.Id, TimeSpan.FromDays(365), null, "Long regulatory extension",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    // ── OverrideAsync — validation failures ───────────────────────────────────

    [Fact]
    public async Task OverrideAsync_returns_missing_actor_identity_when_no_user_id()
    {
        var result = await BuildService().OverrideAsync(
            Guid.NewGuid(), TimeSpan.FromDays(84), null, "reason",
            NoIdentityUser(), TestContext.Current.CancellationToken);

        Assert.Equal(SlaActionFailureCode.MissingActorIdentity, result.FailureCode);
    }

    [Fact]
    public async Task OverrideAsync_returns_invalid_request_when_reason_empty()
    {
        var result = await BuildService().OverrideAsync(
            Guid.NewGuid(), TimeSpan.FromDays(84), null, "",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.Equal(SlaActionFailureCode.InvalidRequest, result.FailureCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task OverrideAsync_returns_invalid_request_for_non_positive_duration(int days)
    {
        var result = await BuildService().OverrideAsync(
            Guid.NewGuid(), TimeSpan.FromDays(days), null, "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.Equal(SlaActionFailureCode.InvalidRequest, result.FailureCode);
    }

    [Fact]
    public async Task OverrideAsync_returns_invalid_request_when_new_started_at_in_future()
    {
        var result = await BuildService().OverrideAsync(
            Guid.NewGuid(), TimeSpan.FromDays(84), UtcNow.AddSeconds(1), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.Equal(SlaActionFailureCode.InvalidRequest, result.FailureCode);
        Assert.Contains("future", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OverrideAsync_returns_not_found_when_work_item_missing()
    {
        var id = Guid.NewGuid();
        _persistence.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);

        var result = await BuildService().OverrideAsync(
            id, TimeSpan.FromDays(84), null, "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.Equal(SlaActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task OverrideAsync_returns_clock_not_started_when_sla_clock_null()
    {
        var workItem = WorkItemWithoutClock();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().OverrideAsync(
            workItem.Id, TimeSpan.FromDays(84), null, "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.Equal(SlaActionFailureCode.ClockNotStarted, result.FailureCode);
    }

    [Fact]
    public async Task OverrideAsync_returns_concurrency_conflict_on_replace_exception()
    {
        var workItem = WorkItemWithClock();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);
        _persistence.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new WorkItemConcurrencyException(workItem.Id, 0));

        var result = await BuildService().OverrideAsync(
            workItem.Id, TimeSpan.FromDays(84), null, "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.Equal(SlaActionFailureCode.ConcurrencyConflict, result.FailureCode);
    }
}
