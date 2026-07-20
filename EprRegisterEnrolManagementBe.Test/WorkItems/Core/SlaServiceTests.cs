using System.Security.Claims;
using System.Xml;
using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    private SlaService BuildService(int maxExtensionDays = 31) =>
        new(
            _persistence,
            NullLogger<SlaService>.Instance,
            BuildOptions(maxExtensionDays),
            _time,
            [_hook]);

    private static IOptionsMonitor<SlaConfig> BuildOptions(int maxDays = 31)
    {
        var monitor = Substitute.For<IOptionsMonitor<SlaConfig>>();
        monitor.CurrentValue.Returns(new SlaConfig { MaxExtensionDays = maxDays });
        return monitor;
    }

    private static ClaimsPrincipal TeamLeader(string userId = "tl-1") =>
        new(new ClaimsIdentity(
        [
            new Claim("cognito:client_id", "test-client"),
            new Claim("user:id", userId),
            new Claim("user:name", "Test Leader"),
            new Claim(ClaimTypes.Role, "standard")
        ], "test"));

    private static ClaimsPrincipal NoIdentityUser() =>
        new(new ClaimsIdentity(
        [
            new Claim("cognito:client_id", "test-client")
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
        Assert.Equal("SLA extended", entry.ActionDisplayName);
        Assert.Equal("tl-alice", entry.CreatedBy);
        Assert.Equal(UtcNow, entry.CreatedAt);
        Assert.Equal("Needs more time", entry.Details["reason"]);
        Assert.Equal("tl-alice", entry.Details["actorUserId"]);
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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ExtendAsync_returns_invalid_request_for_non_positive_duration(int days)
    {
        var result = await BuildService().ExtendAsync(
            Guid.NewGuid(), TimeSpan.FromDays(days), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.Equal(SlaActionFailureCode.InvalidRequest, result.FailureCode);
    }

    [Fact]
    public async Task ExtendAsync_returns_invalid_request_when_duration_exceeds_max()
    {
        var result = await BuildService(maxExtensionDays: 7).ExtendAsync(
            Guid.NewGuid(), TimeSpan.FromDays(8), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.Equal(SlaActionFailureCode.InvalidRequest, result.FailureCode);
        Assert.Contains("7", result.Message);
    }

    [Fact]
    public async Task ExtendAsync_accepts_duration_exactly_at_max()
    {
        var workItem = WorkItemWithClock();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService(maxExtensionDays: 7).ExtendAsync(
            workItem.Id, TimeSpan.FromDays(7), "reason",
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
        var result = await BuildService(maxExtensionDays: 7).OverrideAsync(
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
