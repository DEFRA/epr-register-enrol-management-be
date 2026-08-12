using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-372: the <c>taskStateId</c> wire field, asserted on the responses the
/// management-fe actually consumes rather than on the resolver behind them.
///
/// These exist because coverage was not the same thing as verification here.
/// <c>WorkItemEndpoints.ToResponse</c> already reported 100% line and branch
/// coverage — every endpoint test runs through it, and a projection built
/// without a task state happened to exercise the null-coalesce — while no test
/// asserted the field's value anywhere. Swapping the operands, reading the
/// wrong source, or returning null unconditionally would all have kept that
/// 100% and left the whole suite green.
///
/// The field is load-bearing: management-fe reads it to decide whether an item
/// is parked in a waypoint, and fails closed when it is absent. Both failure
/// directions are silent — always-null and always-<c>stateId</c> each make the
/// Continue review CTA never render, with nothing to signal why.
/// </summary>
public class WorkItemTaskStateResponseMappingTests
{
    private static WorkItem BuildWorkItem(string stateId, string? resumeActionId = null)
    {
        var workItem = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            SubmittedBy = "test-client",
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType()),
        };

        if (resumeActionId is not null)
        {
            workItem.AuditLog.Add(
                new WorkItemAuditEntry
                {
                    Action = "action-applied",
                    ActionDisplayName = "Action applied",
                    CreatedAt = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc),
                    Details = new Dictionary<string, string?>
                    {
                        ["actionId"] = resumeActionId,
                        ["fromStateId"] = "queried",
                        ["toStateId"] = "updated",
                    },
                }
            );
        }

        return workItem;
    }

    private static WorkItemEngineProjection Project(WorkItem workItem) =>
        new WorkItemService(
            new WorkItemRegistry([new ReAccreditationType()]),
            Substitute.For<IWorkItemPersistence>(),
            NullLogger<WorkItemService>.Instance,
            taskStateResolvers: [new ReAccreditationTaskStateResolver()]
        ).Project(workItem);

    // ------------------------------ single item ------------------------------

    [Fact]
    public void ToResponse_reports_the_originating_state_for_an_item_parked_in_updated()
    {
        var workItem = BuildWorkItem("updated", "resume-during-assessment");

        var response = WorkItemEndpoints.ToResponse(Project(workItem));

        Assert.Equal("assessment-in-progress", response.TaskStateId);
        // The item has not moved — only the checklist it is showing.
        Assert.Equal("updated", response.StateId);
        Assert.NotEqual(response.StateId, response.TaskStateId);
        // And the tasks really are the originating state's, so the field
        // describes the list beside it rather than being independently right.
        Assert.Equal(
            ["review-compliance-history", "assess-technical-capacity", "assess-financial-capacity"],
            response.Tasks.Select(t => t.TaskId)
        );
    }

    [Theory]
    [InlineData("submitted")]
    [InlineData("duly-made")]
    [InlineData("assessment-in-progress")]
    [InlineData("awaiting-decision")]
    [InlineData("queried")]
    public void ToResponse_reports_the_items_own_state_when_nothing_is_redirected(string stateId)
    {
        var response = WorkItemEndpoints.ToResponse(Project(BuildWorkItem(stateId)));

        Assert.Equal(stateId, response.TaskStateId);
        Assert.Equal(response.StateId, response.TaskStateId);
    }

    /// <summary>
    /// The null-coalesce, asserted directly. A projection carrying no task
    /// state must still yield a populated field — the frontend fails closed on
    /// absence, so a null here silently disables the CTA rather than erroring.
    /// </summary>
    [Fact]
    public void ToResponse_falls_back_to_the_item_state_for_a_projection_without_a_task_state()
    {
        var workItem = BuildWorkItem("assessment-in-progress");
        var projection = new WorkItemEngineProjection(
            workItem,
            "v10",
            [],
            [],
            TaskStateId: null
        );

        var response = WorkItemEndpoints.ToResponse(projection);

        Assert.Equal("assessment-in-progress", response.TaskStateId);
    }

    // -------------------------------- list --------------------------------

    [Fact]
    public void ToListItemResponse_reports_the_originating_state_for_an_item_parked_in_updated()
    {
        var workItem = BuildWorkItem("updated", "resume-during-duly-made");

        var response = WorkItemEndpoints.ToListItemResponse(Project(workItem));

        Assert.Equal("duly-made", response.TaskStateId);
        Assert.Equal("updated", response.StateId);
        Assert.Equal(["confirm-registration-fee-paid"], response.Tasks.Select(t => t.TaskId));
    }

    [Fact]
    public void ToListItemResponse_reports_the_items_own_state_when_nothing_is_redirected()
    {
        var response = WorkItemEndpoints.ToListItemResponse(
            Project(BuildWorkItem("assessment-in-progress"))
        );

        Assert.Equal("assessment-in-progress", response.TaskStateId);
        Assert.Equal(response.StateId, response.TaskStateId);
    }

    [Fact]
    public void ToListItemResponse_falls_back_to_the_item_state_without_a_task_state()
    {
        var projection = new WorkItemEngineProjection(
            BuildWorkItem("duly-made"),
            "v10",
            [],
            [],
            TaskStateId: null
        );

        var response = WorkItemEndpoints.ToListItemResponse(projection);

        Assert.Equal("duly-made", response.TaskStateId);
    }

    /// <summary>
    /// The two shapes must agree — management-fe reads the same field from a
    /// list row and from the detail page, and a divergence would show a
    /// caseworker one thing in the worklist and another on the case.
    /// </summary>
    [Fact]
    public void Both_shapes_report_the_same_task_state_for_the_same_item()
    {
        var projection = Project(BuildWorkItem("updated", "resume-during-decision"));

        Assert.Equal(
            WorkItemEndpoints.ToResponse(projection).TaskStateId,
            WorkItemEndpoints.ToListItemResponse(projection).TaskStateId
        );
        Assert.Equal("awaiting-decision", WorkItemEndpoints.ToResponse(projection).TaskStateId);
    }
}
