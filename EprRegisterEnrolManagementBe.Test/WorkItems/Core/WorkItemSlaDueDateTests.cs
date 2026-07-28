using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-324: coverage for the absolute SLA due date the Applications card renders
/// ("Due on:"). Unlike the relative <c>SlaRemaining</c>, the deadline is a
/// fixed instant (<c>slaClock.StartedAt + TargetDuration</c>) and needs no
/// "now".
/// </summary>
public class WorkItemSlaDueDateTests
{
    private static readonly DateTime s_startedAt =
        new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);

    private static WorkItem ItemWith(WorkItemSlaClock? clock) => new()
    {
        TypeId = "re-accreditation",
        StateId = "assessment-in-progress",
        SubmittedAt = s_startedAt,
        LastModifiedAt = s_startedAt,
        SlaClock = clock
    };

    private static WorkItemEngineProjection Project(WorkItem item) =>
        new(item, "v9", Array.Empty<WorkItemTaskProgress>(), Array.Empty<WorkItemTransition>());

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
}
