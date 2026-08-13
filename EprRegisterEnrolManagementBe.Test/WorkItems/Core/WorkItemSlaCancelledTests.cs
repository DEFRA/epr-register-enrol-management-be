using System.Text.Json;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-359 (part 2): a work item in a terminal state (withdrawn / approved /
/// rejected) has its SLA cancelled. This is a read-layer projection fix — the
/// persisted <see cref="WorkItemSlaClock"/> is never mutated on the transition
/// (so the historical deadline survives), but the derived
/// <c>SlaState</c>/<c>SlaRemaining</c> stop reporting a running SLA and instead
/// report <see cref="WorkItemSlaState.Cancelled"/>.
/// </summary>
public class WorkItemSlaCancelledTests
{
    private static readonly DateTime s_startedAt =
        new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);

    // StartedAt + 84 days = 2026-03-26T09:00:00Z.
    private static WorkItemSlaClock RunningClock() => new()
    {
        StartedAt = s_startedAt,
        TargetDuration = TimeSpan.FromDays(84)
    };

    // ── ComputeSla: terminal awareness ───────────────────────────────────────

    [Fact]
    public void ComputeSla_terminal_item_with_running_clock_is_Cancelled()
    {
        // Well inside the window — would be OnTrack if it were still running.
        var now = s_startedAt.AddDays(10);

        var (remaining, state) =
            WorkItemEndpoints.ComputeSla(RunningClock(), now, isTerminal: true);

        Assert.Equal(WorkItemSlaState.Cancelled, state);
        Assert.Null(remaining);
    }

    [Fact]
    public void ComputeSla_terminal_overrides_a_would_be_Breached_clock()
    {
        // Past the deadline: a running clock here reports Breached. Terminality
        // must win — a withdrawn item is Cancelled, not Breached.
        var now = s_startedAt.AddDays(200);

        var (remaining, state) =
            WorkItemEndpoints.ComputeSla(RunningClock(), now, isTerminal: true);

        Assert.Equal(WorkItemSlaState.Cancelled, state);
        Assert.Null(remaining);
    }

    [Fact]
    public void ComputeSla_terminal_with_a_flagged_breach_is_still_Cancelled()
    {
        var clock = RunningClock();
        clock.Breached = true; // set by the nightly job before withdrawal

        var (remaining, state) = WorkItemEndpoints.ComputeSla(
            clock, s_startedAt.AddDays(10), isTerminal: true);

        Assert.Equal(WorkItemSlaState.Cancelled, state);
        Assert.Null(remaining);
    }

    [Fact]
    public void ComputeSla_terminal_item_with_no_clock_reports_no_state()
    {
        // No clock ever started ⇒ no SLA to cancel. Reported exactly like a
        // non-terminal item with no clock: null state, null remaining.
        var (remaining, state) = WorkItemEndpoints.ComputeSla(
            null, s_startedAt.AddDays(10), isTerminal: true);

        Assert.Null(state);
        Assert.Null(remaining);
    }

    [Fact]
    public void ComputeSla_terminal_without_now_is_still_Cancelled()
    {
        // Terminality is time-independent: even without a "now" (the
        // non-time-provider call path) a terminal clock cancels.
        var (remaining, state) =
            WorkItemEndpoints.ComputeSla(RunningClock(), now: null, isTerminal: true);

        Assert.Equal(WorkItemSlaState.Cancelled, state);
        Assert.Null(remaining);
    }

    // ── ComputeSla: non-terminal behaviour is unchanged ──────────────────────

    [Fact]
    public void ComputeSla_non_terminal_running_clock_is_OnTrack()
    {
        var (remaining, state) = WorkItemEndpoints.ComputeSla(
            RunningClock(), s_startedAt.AddDays(10), isTerminal: false);

        Assert.Equal(WorkItemSlaState.OnTrack, state);
        Assert.NotNull(remaining);
        Assert.True(remaining > TimeSpan.Zero);
    }

    [Fact]
    public void ComputeSla_non_terminal_near_deadline_is_AtRisk()
    {
        // 10 days remain (≤ 14, > 0).
        var (_, state) = WorkItemEndpoints.ComputeSla(
            RunningClock(), s_startedAt.AddDays(74), isTerminal: false);

        Assert.Equal(WorkItemSlaState.AtRisk, state);
    }

    [Fact]
    public void ComputeSla_non_terminal_past_deadline_is_Breached()
    {
        var (_, state) = WorkItemEndpoints.ComputeSla(
            RunningClock(), s_startedAt.AddDays(200), isTerminal: false);

        Assert.Equal(WorkItemSlaState.Breached, state);
    }

    [Fact]
    public void ComputeSla_defaults_to_non_terminal()
    {
        // The isTerminal parameter defaults to false, so the existing two-arg
        // call sites keep their pre-RA-359 behaviour.
        var (_, state) =
            WorkItemEndpoints.ComputeSla(RunningClock(), s_startedAt.AddDays(10));

        Assert.Equal(WorkItemSlaState.OnTrack, state);
    }

    // ── Projection: single-item and list responses ───────────────────────────

    [Fact]
    public void ToResponse_terminal_projection_cancels_but_keeps_the_due_date()
    {
        var response = WorkItemEndpoints.ToResponse(
            TerminalProjection(RunningClock()),
            new FixedTimeProvider(s_startedAt.AddDays(10)));

        Assert.Equal(WorkItemSlaState.Cancelled, response.SlaState);
        Assert.Null(response.SlaRemaining);
        // Historical deadline preserved (RA-359 AC2: do not lose it).
        Assert.Equal(s_startedAt.AddDays(84), response.SlaDueDate);
    }

    [Fact]
    public void ToListItemResponse_terminal_projection_cancels_but_keeps_the_due_date()
    {
        var response = WorkItemEndpoints.ToListItemResponse(
            TerminalProjection(RunningClock()),
            new FixedTimeProvider(s_startedAt.AddDays(10)));

        Assert.Equal(WorkItemSlaState.Cancelled, response.SlaState);
        Assert.Null(response.SlaRemaining);
        Assert.Equal(s_startedAt.AddDays(84), response.SlaDueDate);
    }

    [Fact]
    public void ToResponse_non_terminal_projection_is_unchanged()
    {
        var response = WorkItemEndpoints.ToResponse(
            NonTerminalProjection(RunningClock()),
            new FixedTimeProvider(s_startedAt.AddDays(10)));

        Assert.Equal(WorkItemSlaState.OnTrack, response.SlaState);
        Assert.NotNull(response.SlaRemaining);
    }

    [Fact]
    public void ToResponse_terminal_with_no_clock_reports_no_sla()
    {
        var response = WorkItemEndpoints.ToResponse(
            TerminalProjection(clock: null),
            new FixedTimeProvider(s_startedAt.AddDays(10)));

        Assert.Null(response.SlaState);
        Assert.Null(response.SlaRemaining);
        Assert.Null(response.SlaDueDate);
    }

    // ── WorkItemService.Project wires terminality from TerminalStates.Find ────

    [Fact]
    public void Project_marks_a_withdrawn_item_terminal_and_cancels_its_sla()
    {
        var service = BuildService();
        var item = ItemInState("withdrawn");

        var projection = service.Project(item);
        Assert.True(projection.IsTerminal);

        var response = WorkItemEndpoints.ToResponse(
            projection, new FixedTimeProvider(s_startedAt.AddDays(10)));
        Assert.Equal(WorkItemSlaState.Cancelled, response.SlaState);
    }

    [Fact]
    public void Project_leaves_a_non_terminal_item_running()
    {
        var service = BuildService();
        var item = ItemInState("in-progress");

        var projection = service.Project(item);
        Assert.False(projection.IsTerminal);

        var response = WorkItemEndpoints.ToResponse(
            projection, new FixedTimeProvider(s_startedAt.AddDays(10)));
        Assert.Equal(WorkItemSlaState.OnTrack, response.SlaState);
    }

    // ── Serialisation contract (cross-repo: FE + e2e depend on this) ──────────

    [Fact]
    public void Cancelled_serialises_as_the_string_Cancelled_on_both_wire_shapes()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var tp = new FixedTimeProvider(s_startedAt.AddDays(10));

        var single = JsonSerializer.SerializeToElement(
            WorkItemEndpoints.ToResponse(TerminalProjection(RunningClock()), tp), options);
        var listItem = JsonSerializer.SerializeToElement(
            WorkItemEndpoints.ToListItemResponse(TerminalProjection(RunningClock()), tp),
            options);

        Assert.Equal("Cancelled", single.GetProperty("slaState").GetString());
        Assert.Equal("Cancelled", listItem.GetProperty("slaState").GetString());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static WorkItem ItemInState(string stateId) => new()
    {
        Id = Guid.NewGuid(),
        TypeId = "test-type",
        StateId = stateId,
        SubmittedAt = s_startedAt,
        LastModifiedAt = s_startedAt,
        SlaClock = RunningClock()
    };

    private static WorkItemEngineProjection TerminalProjection(WorkItemSlaClock? clock) =>
        new(
            ItemWith(clock),
            "v9",
            Array.Empty<WorkItemTransition>(),
            OriginStateId: null,
            IsTerminal: true);

    private static WorkItemEngineProjection NonTerminalProjection(WorkItemSlaClock? clock) =>
        new(
            ItemWith(clock),
            "v9",
            Array.Empty<WorkItemTransition>(),
            OriginStateId: null,
            IsTerminal: false);

    private static WorkItem ItemWith(WorkItemSlaClock? clock) => new()
    {
        Id = Guid.NewGuid(),
        TypeId = "test-type",
        StateId = "withdrawn",
        SubmittedAt = s_startedAt,
        LastModifiedAt = s_startedAt,
        SlaClock = clock
    };

    private static WorkItemService BuildService() =>
        new(
            new WorkItemRegistry(
            [
                new TestWorkItemType(
                    "test-type",
                    "Test type",
                    initialState: new WorkItemState("in-progress", "In progress"),
                    states:
                    [
                        new WorkItemState("in-progress", "In progress"),
                        new WorkItemState("withdrawn", "Withdrawn", IsTerminal: true)
                    ])
            ]),
            Substitute.For<IWorkItemPersistence>(),
            NullLogger<WorkItemService>.Instance);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
