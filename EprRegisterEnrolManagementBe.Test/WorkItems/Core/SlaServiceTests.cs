using System.Security.Claims;
using System.Xml;
using MongoDB.Bson;
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

    private SlaService BuildService(TimeProvider? timeProvider = null) =>
        new(
            _persistence,
            NullLogger<SlaService>.Instance,
            timeProvider ?? _time,
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

    /// <summary>
    /// RA-611: <paramref name="accreditationYear"/> populates
    /// <c>payload.accreditationYear</c>, the source of the deadline floor's
    /// 1-January bound. Left null the payload carries no such key at all, which
    /// is the shape of a work item created through the case management UI (and
    /// the shape every test predating RA-611 uses). Typed as a
    /// <see cref="BsonValue"/> so a test can also pass a non-numeric value.
    /// </summary>
    private static WorkItem WorkItemWithClock(
        Guid? id = null,
        TimeSpan? targetDuration = null,
        DateTime? startedAt = null,
        bool breached = false,
        BsonValue? accreditationYear = null) =>
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
            },
            Payload = accreditationYear is null
                ? new BsonDocument()
                : new BsonDocument { ["accreditationYear"] = accreditationYear }
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

        // This service reads the REAL clock. RA-611's floor no longer consults
        // it, but LastModifiedAt does, and the assertion below is what proves
        // TimeProvider.System (not a fixed fake) was used — so the clock still
        // starts at real wall-clock time.
        var workItem = WorkItemWithClock(startedAt: DateTime.UtcNow);
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
        Assert.Equal("Determination deadline changed", entry.ActionDisplayName);
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

    /// <summary>
    /// RA-572 AC02: the Application history must read in change terminology,
    /// not extend terminology. The display name is user-visible and was
    /// reworded; the <c>sla-extended</c> action value is a STORED
    /// discriminator that management-fe switches on in
    /// <c>detailRowsForAuditEntry</c>, so renaming it would orphan every
    /// historical audit entry. The pairing is the load-bearing part.
    /// </summary>
    [Fact]
    public async Task ExtendAsync_audit_entry_reads_changed_while_action_discriminator_stays_sla_extended()
    {
        var workItem = WorkItemWithClock();
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(7), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        var entry = Assert.Single(result.WorkItem!.AuditLog);
        Assert.Equal("sla-extended", entry.Action);
        Assert.Equal("Determination deadline changed", entry.ActionDisplayName);
        Assert.DoesNotContain(
            "extend",
            entry.ActionDisplayName!,
            StringComparison.OrdinalIgnoreCase);
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

    // ── RA-601/RA-611: moving the determination deadline EARLIER ─────────────
    //
    // RA-611's floor is the LATER of the duly-made date (the SLA clock's start,
    // as a Europe/London date) and 1 January of payload.accreditationYear.
    // Landing exactly on it is accepted. The spec changed mid-branch — the first
    // cut floored at today — so the tests below were re-pointed rather than
    // rewritten, and the ones that now ACCEPT a past deadline are the ones that
    // prove the replacement actually happened.

    [Fact]
    public async Task ExtendAsync_accepts_a_negative_duration_and_moves_the_deadline_earlier()
    {
        // RA-611 keeps this: the clock started 10 days ago with an 84-day
        // target, so pulling 14 days off still leaves the deadline 60 days in
        // the future. Backwards moves onto a future date remain legal.
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
    public async Task ExtendAsync_accepts_a_deadline_in_the_past_above_the_floor()
    {
        // The spec changed mid-branch: RA-611's first cut floored the deadline
        // at TODAY, and this test asserted the rejection. Backdating is legal
        // again — the clock started 10 days ago with an 84-day target, so
        // pulling 80 days off lands the deadline 6 days in the PAST, above the
        // duly-made floor, and is accepted. This is the case most easily lost
        // when only the rejection paths are re-pointed.
        var workItem = WorkItemWithClock(targetDuration: TimeSpan.FromDays(84));
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(-80), "Backdated to the assessment date",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.FromDays(4), result.WorkItem!.SlaClock!.TargetDuration);
        var dueAt = result.WorkItem.SlaClock.DueAt;
        Assert.Equal(new DateTime(2026, 5, 13, 12, 0, 0, DateTimeKind.Utc), dueAt);
        Assert.True(dueAt < UtcNow, "the accepted deadline is in the past");
        await _persistence.Received(1).ReplaceAsync(workItem, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtendAsync_accepts_a_deadline_landing_exactly_on_the_duly_made_date()
    {
        // Landing ON the floor is accepted. -84 days zeroes the target, so the
        // deadline is the clock start itself — which is also why ExtendAsync can
        // still write a non-positive TargetDuration (see WorkItemSlaClock.DueAt):
        // the floor compares DATES, not instants.
        var startedAt = UtcNow.AddDays(-10);
        var workItem = WorkItemWithClock(
            targetDuration: TimeSpan.FromDays(84), startedAt: startedAt);
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(-84), "Determined on the duly-made date",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.Zero, result.WorkItem!.SlaClock!.TargetDuration);
        Assert.Equal(startedAt, result.WorkItem.SlaClock.DueAt);
        await _persistence.Received(1).ReplaceAsync(workItem, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(-85)]  // one day below the duly-made date
    [InlineData(-200)] // far below it
    public async Task ExtendAsync_rejects_a_deadline_earlier_than_the_duly_made_date(int days)
    {
        // The clock started on 09-May-2026, so that is the duly-made date and
        // the floor. The message names it so the caseworker knows where the
        // floor is instead of guessing; the work item is left untouched.
        var workItem = WorkItemWithClock(targetDuration: TimeSpan.FromDays(84));
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(days), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.Equal(SlaActionFailureCode.InvalidRequest, result.FailureCode);
        Assert.Equal(
            "The new determination deadline cannot be earlier than 9 May 2026, " +
            "when the application was duly made.",
            result.Message);
        Assert.Equal(TimeSpan.FromDays(84), workItem.SlaClock!.TargetDuration);
        Assert.Empty(workItem.AuditLog);
        await _persistence.DidNotReceive().ReplaceAsync(
            Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
        await _hook.DidNotReceive().OnActionAppliedAsync(
            Arg.Any<WorkItem>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtendAsync_rejects_a_deadline_earlier_than_1_january_of_the_accreditation_year()
    {
        // The duly-made date is 14-Nov-2025 but the accreditation year is 2026,
        // so 1 January 2026 is the LATER bound and the one that binds. The
        // resulting deadline (28-Dec-2025) sits above the duly-made date — the
        // duly-made bound on its own would have allowed it — so this test fails
        // if the 1-January bound is dropped or is applied as the earlier of the
        // two. The message names 1 January, not the duly-made date.
        var workItem = WorkItemWithClock(
            targetDuration: TimeSpan.FromDays(84),
            startedAt: new DateTime(2025, 11, 14, 0, 0, 0, DateTimeKind.Utc),
            accreditationYear: 2026);
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(-40), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.Equal(SlaActionFailureCode.InvalidRequest, result.FailureCode);
        Assert.Equal(
            "The new determination deadline cannot be earlier than 1 January 2026.",
            result.Message);
        Assert.Equal(TimeSpan.FromDays(84), workItem.SlaClock!.TargetDuration);
        Assert.Empty(workItem.AuditLog);
        await _persistence.DidNotReceive().ReplaceAsync(
            Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtendAsync_accepts_a_deadline_landing_exactly_on_1_january_of_the_accreditation_year()
    {
        // The other side of the same boundary: 48 days from a 14-Nov-2025 start
        // is 01-Jan-2026 exactly, which is on the floor and therefore accepted.
        var workItem = WorkItemWithClock(
            targetDuration: TimeSpan.FromDays(84),
            startedAt: new DateTime(2025, 11, 14, 0, 0, 0, DateTimeKind.Utc),
            accreditationYear: 2026);
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(-36), "Determined on the first of the year",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.FromDays(48), result.WorkItem!.SlaClock!.TargetDuration);
        Assert.Equal(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            result.WorkItem.SlaClock.DueAt);
    }

    [Fact]
    public async Task ExtendAsync_uses_the_duly_made_date_when_it_is_later_than_1_january()
    {
        // The usual shape for a live case: the application was duly made on
        // 03-Mar-2026, inside accreditation year 2026, so the duly-made date is
        // the later bound. A deadline of 25-Feb-2026 clears 1 January but not
        // the duly-made date, and the message must name the bound that actually
        // bound.
        var workItem = WorkItemWithClock(
            targetDuration: TimeSpan.FromDays(84),
            startedAt: new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc),
            accreditationYear: 2026);
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(-90), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.Equal(SlaActionFailureCode.InvalidRequest, result.FailureCode);
        Assert.Equal(
            "The new determination deadline cannot be earlier than 3 March 2026, " +
            "when the application was duly made.",
            result.Message);
    }

    [Fact]
    public async Task ExtendAsync_names_1_january_when_both_bounds_fall_on_the_same_date()
    {
        // Tie-break: an application duly made on 01-Jan-2026 in accreditation
        // year 2026 has both bounds on the same date. The 1-January wording is
        // emitted, which names the same date either way — pinned so the
        // frontend's mirror of this rule can rely on it.
        var workItem = WorkItemWithClock(
            targetDuration: TimeSpan.FromDays(84),
            startedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            accreditationYear: 2026);
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(-90), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.Equal(SlaActionFailureCode.InvalidRequest, result.FailureCode);
        Assert.Equal(
            "The new determination deadline cannot be earlier than 1 January 2026.",
            result.Message);
    }

    [Fact]
    public async Task ExtendAsync_falls_back_to_the_duly_made_bound_when_the_accreditation_year_is_absent()
    {
        // Same numbers as the 1-January rejection above, but with no
        // accreditationYear on the payload. "Now" is May 2026, so a
        // current-year fallback — which ApplicationReferenceGenerator.ResolveYear
        // does for reference generation — would impose 1 January 2026 and reject
        // this. ExtendAsync deliberately does not: it would mean refusing a
        // legitimate backdate on the strength of a floor nobody told the
        // caseworker about. Duly-made alone, so 28-Dec-2025 is accepted.
        var workItem = WorkItemWithClock(
            targetDuration: TimeSpan.FromDays(84),
            startedAt: new DateTime(2025, 11, 14, 0, 0, 0, DateTimeKind.Utc));
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(-40), "Backdated within the previous year",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new DateTime(2025, 12, 28, 0, 0, 0, DateTimeKind.Utc),
            result.WorkItem!.SlaClock!.DueAt);
    }

    [Theory]
    [InlineData("2026")]       // numeric-looking, but a BSON string
    [InlineData("not-a-year")]
    public async Task ExtendAsync_ignores_a_non_numeric_accreditation_year(string accreditationYear)
    {
        // Same assertion as the absent case: a payload whose accreditationYear
        // is not a BSON number contributes no 1-January bound rather than being
        // coerced or defaulted.
        var workItem = WorkItemWithClock(
            targetDuration: TimeSpan.FromDays(84),
            startedAt: new DateTime(2025, 11, 14, 0, 0, 0, DateTimeKind.Utc),
            accreditationYear: accreditationYear);
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(-40), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new DateTime(2025, 12, 28, 0, 0, 0, DateTimeKind.Utc),
            result.WorkItem!.SlaClock!.DueAt);
    }

    [Theory]
    [InlineData(0)]       // DateOnly has no year 0
    [InlineData(100_000)] // beyond DateOnly's range
    public async Task ExtendAsync_ignores_an_accreditation_year_outside_the_representable_range(
        int accreditationYear)
    {
        // A numeric but impossible year is treated as absent rather than
        // throwing out of new DateOnly(year, 1, 1) — a junk payload field must
        // not 500 a deadline change.
        var workItem = WorkItemWithClock(
            targetDuration: TimeSpan.FromDays(84),
            startedAt: new DateTime(2025, 11, 14, 0, 0, 0, DateTimeKind.Utc),
            accreditationYear: accreditationYear);
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id, TimeSpan.FromDays(-40), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new DateTime(2025, 12, 28, 0, 0, 0, DateTimeKind.Utc),
            result.WorkItem!.SlaClock!.DueAt);
    }

    [Fact]
    public async Task ExtendAsync_derives_the_duly_made_bound_from_the_uk_date_of_the_clock_start()
    {
        // BST edge on the ANCHOR. The clock starts at 23:30 UTC on 08-May-2026,
        // which is 00:30 on the 9th in London — so the duly-made date is the
        // 9th. A deadline at 12:00 UTC on the 8th is the 8th in both zones and
        // must be refused, naming the 9th. A UTC-date reading of the anchor
        // would put the floor on the 8th and accept this, which is exactly the
        // off-by-one-day the Europe/London conversion exists to prevent.
        var workItem = WorkItemWithClock(
            targetDuration: TimeSpan.FromDays(84),
            startedAt: new DateTime(2026, 5, 8, 23, 30, 0, DateTimeKind.Utc));
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id,
            TimeSpan.FromDays(-84) - TimeSpan.FromHours(11.5),
            "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.Equal(SlaActionFailureCode.InvalidRequest, result.FailureCode);
        Assert.Equal(
            "The new determination deadline cannot be earlier than 9 May 2026, " +
            "when the application was duly made.",
            result.Message);
        await _persistence.DidNotReceive().ReplaceAsync(
            Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtendAsync_derives_the_duly_made_bound_from_the_uk_date_outside_british_summer_time()
    {
        // The GMT half of the pair. Same 23:30 clock start and same shift, but
        // in January: London is UTC, so the duly-made date is the 8th and a
        // deadline at 12:00 UTC on the 8th lands ON the floor and is accepted.
        // A blanket "+1 hour" treatment of the anchor would refuse it.
        var workItem = WorkItemWithClock(
            targetDuration: TimeSpan.FromDays(84),
            startedAt: new DateTime(2027, 1, 8, 23, 30, 0, DateTimeKind.Utc));
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService().ExtendAsync(
            workItem.Id,
            TimeSpan.FromDays(-84) - TimeSpan.FromHours(11.5),
            "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new DateTime(2027, 1, 8, 12, 0, 0, DateTimeKind.Utc),
            result.WorkItem!.SlaClock!.DueAt);
    }

    [Fact]
    public async Task ExtendAsync_floor_does_not_depend_on_the_current_date()
    {
        // The today-floor is gone, not merely relaxed: the floor is a property
        // of the work item (its clock start and accreditation year), so driving
        // "now" four years forward changes nothing about whether a deadline of
        // 13-May-2026 is allowed. TimeProvider still stamps LastModifiedAt, and
        // this pins that as its only remaining role in ExtendAsync.
        var farFuture = new DateTime(2030, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var workItem = WorkItemWithClock(targetDuration: TimeSpan.FromDays(84));
        _persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);

        var result = await BuildService(new FakeTimeProvider(farFuture)).ExtendAsync(
            workItem.Id, TimeSpan.FromDays(-80), "reason",
            TeamLeader(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new DateTime(2026, 5, 13, 12, 0, 0, DateTimeKind.Utc),
            result.WorkItem!.SlaClock!.DueAt);
        Assert.Equal(farFuture, result.WorkItem.LastModifiedAt);
    }

    [Theory]
    [InlineData(-4_000_000)] // deadline underflows DateTime.MinValue
    [InlineData(4_000_000)]  // deadline overflows DateTime.MaxValue
    public async Task ExtendAsync_rejects_a_deadline_outside_the_representable_date_range(int days)
    {
        // Not the RA-611 floor — this is the edge of what a DateTime can hold,
        // and it is checked first so an unrepresentable date is reported as
        // such. Nothing throws; the caller gets a 422-mapped InvalidRequest and
        // the work item is left untouched.
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
