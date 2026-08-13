using System.Security.Claims;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-324 / RA-295: coverage for the absolute SLA due date the Applications
/// card and the individual case header render ("Due on:"). Unlike the relative
/// <c>SlaRemaining</c>, the deadline is a fixed instant
/// (<c>slaClock.StartedAt + TargetDuration</c>) and needs no "now". RA-295
/// extends it to the single-item <see cref="WorkItemResponse"/>.
/// </summary>
public class WorkItemSlaDueDateTests
{
    private static readonly DateTime s_startedAt =
        new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);

    private static WorkItem ItemWith(WorkItemSlaClock? clock, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        TypeId = "re-accreditation",
        StateId = "assessment-in-progress",
        SubmittedAt = s_startedAt,
        LastModifiedAt = s_startedAt,
        SlaClock = clock
    };

    private static WorkItemEngineProjection Project(WorkItem item) =>
        new(item, "v9", Array.Empty<WorkItemTransition>());

    [Fact]
    public void ComputeSlaDueDate_is_null_when_no_clock()
    {
        Assert.Null(WorkItemEndpoints.ComputeSlaDueDate(null));
    }

    [Fact]
    public void ComputeSlaDueDate_is_start_plus_target_duration()
    {
        var clock = new WorkItemSlaClock
        {
            StartedAt = s_startedAt,
            TargetDuration = TimeSpan.FromDays(84)
        };

        var due = WorkItemEndpoints.ComputeSlaDueDate(clock);

        Assert.Equal(s_startedAt.AddDays(84), due);
    }

    [Fact]
    public void ToListItemResponse_projects_the_absolute_due_date()
    {
        var clock = new WorkItemSlaClock
        {
            StartedAt = s_startedAt,
            TargetDuration = TimeSpan.FromDays(84)
        };

        var response = WorkItemEndpoints.ToListItemResponse(Project(ItemWith(clock)));

        Assert.Equal(s_startedAt.AddDays(84), response.SlaDueDate);
    }

    [Fact]
    public void ToListItemResponse_due_date_is_null_without_a_clock()
    {
        var response = WorkItemEndpoints.ToListItemResponse(Project(ItemWith(null)));

        Assert.Null(response.SlaDueDate);
    }

    // ── RA-295: the same absolute deadline on the single-item response ────────

    [Fact]
    public void ToResponse_projects_the_absolute_due_date()
    {
        var clock = new WorkItemSlaClock
        {
            StartedAt = s_startedAt,
            TargetDuration = TimeSpan.FromDays(84)
        };

        var response = WorkItemEndpoints.ToResponse(Project(ItemWith(clock)));

        Assert.Equal(s_startedAt.AddDays(84), response.SlaDueDate);
    }

    [Fact]
    public void ToResponse_due_date_is_null_without_a_clock()
    {
        var response = WorkItemEndpoints.ToResponse(Project(ItemWith(null)));

        Assert.Null(response.SlaDueDate);
        // The relative countdown fields stay null too — the case header
        // renders a dash for all three rather than a bogus 0001-01-01.
        Assert.Null(response.SlaRemaining);
        Assert.Null(response.SlaState);
    }

    /// <summary>
    /// The due date must be read off the *live* clock, not a value snapshotted
    /// at submission: <see cref="ISlaService.ExtendAsync"/> mutates
    /// <c>TargetDuration</c> in place, so a 14-day extension must move the
    /// projected deadline 14 days later.
    /// </summary>
    [Fact]
    public async Task Sla_extend_moves_the_projected_due_date()
    {
        var item = ItemWith(new WorkItemSlaClock
        {
            StartedAt = s_startedAt,
            TargetDuration = TimeSpan.FromDays(84)
        });

        var before = WorkItemEndpoints.ToResponse(Project(item)).SlaDueDate;
        Assert.Equal(s_startedAt.AddDays(84), before);

        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.GetByIdAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(item);
        var options = Substitute.For<IOptionsMonitor<SlaConfig>>();
        options.CurrentValue.Returns(new SlaConfig { MaxExtensionDays = 31 });
        var service = new SlaService(
            persistence,
            NullLogger<SlaService>.Instance,
            options,
            new FixedTimeProvider(s_startedAt.AddDays(10)));

        var result = await service.ExtendAsync(
            item.Id,
            TimeSpan.FromDays(14),
            "Awaiting operator evidence",
            Caseworker(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var after = WorkItemEndpoints.ToResponse(Project(result.WorkItem!)).SlaDueDate;
        Assert.Equal(before!.Value.AddDays(14), after);
    }

    /// <summary>
    /// An override replaces both the start and the target duration, so the
    /// projected deadline must follow the new clock rather than the original.
    /// </summary>
    [Fact]
    public async Task Sla_override_repoints_the_projected_due_date()
    {
        var item = ItemWith(new WorkItemSlaClock
        {
            StartedAt = s_startedAt,
            TargetDuration = TimeSpan.FromDays(84)
        });

        var newStartedAt = s_startedAt.AddDays(30);
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.GetByIdAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(item);
        var options = Substitute.For<IOptionsMonitor<SlaConfig>>();
        options.CurrentValue.Returns(new SlaConfig { MaxExtensionDays = 31 });
        var service = new SlaService(
            persistence,
            NullLogger<SlaService>.Instance,
            options,
            new FixedTimeProvider(s_startedAt.AddDays(60)));

        var result = await service.OverrideAsync(
            item.Id,
            TimeSpan.FromDays(21),
            newStartedAt,
            "Agreed with the operator",
            Caseworker(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var after = WorkItemEndpoints.ToResponse(Project(result.WorkItem!)).SlaDueDate;
        Assert.Equal(newStartedAt.AddDays(21), after);
    }

    /// <summary>
    /// The BFF reads the deadline by its exact JSON key on both wire shapes,
    /// so pin the serialised name (and the ISO-8601 UTC instant format) rather
    /// than relying on the C# property name alone. Minimal APIs serialise with
    /// <see cref="JsonSerializerDefaults.Web"/> — this service registers no
    /// custom JSON options — so these are the bytes callers receive.
    /// </summary>
    [Fact]
    public void Due_date_serialises_as_slaDueDate_on_both_wire_shapes()
    {
        var clock = new WorkItemSlaClock
        {
            StartedAt = s_startedAt,
            TargetDuration = TimeSpan.FromDays(84)
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var single = JsonSerializer.SerializeToElement(
            WorkItemEndpoints.ToResponse(Project(ItemWith(clock))), options);
        var listItem = JsonSerializer.SerializeToElement(
            WorkItemEndpoints.ToListItemResponse(Project(ItemWith(clock))), options);

        Assert.Equal(
            "2026-03-26T09:00:00Z",
            single.GetProperty("slaDueDate").GetString());
        Assert.Equal(
            "2026-03-26T09:00:00Z",
            listItem.GetProperty("slaDueDate").GetString());
    }

    [Fact]
    public void Due_date_serialises_as_json_null_when_no_clock_started()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var single = JsonSerializer.SerializeToElement(
            WorkItemEndpoints.ToResponse(Project(ItemWith(null))), options);

        // Present-and-null (not omitted), so a caller can distinguish
        // "no SLA clock yet" from an unexpected shape.
        Assert.Equal(
            JsonValueKind.Null,
            single.GetProperty("slaDueDate").ValueKind);
    }

    private static ClaimsPrincipal Caseworker() =>
        new(new ClaimsIdentity(
        [
            new Claim("client_id", "test-client"),
            new Claim("user:id", "cw-1"),
            new Claim("user:name", "Case Worker")
        ], "test"));

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
