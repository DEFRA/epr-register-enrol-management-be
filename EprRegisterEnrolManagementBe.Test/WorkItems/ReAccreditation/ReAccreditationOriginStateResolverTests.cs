using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-410: the waypoint-origin question outlived the task framework that used
/// to ask it. An application queried while it was being duly made, then
/// resubmitted, sits in <c>updated</c> — and the only way to know it belongs
/// back in <c>submitted</c> (and may therefore be offered "Duly make") is the
/// item's own audit history. The case management frontend cannot derive that,
/// so the backend reports it as <c>OriginStateId</c>.
///
/// Losing this is not cosmetic: with no origin the frontend refuses "Duly
/// make" for every resubmitted application, stranding them in <c>updated</c>
/// with no route to <c>duly-made</c>.
/// </summary>
public class ReAccreditationOriginStateResolverTests
{
    [Theory]
    [InlineData("resume-during-duly-making", "submitted")]
    [InlineData("resume-during-duly-made", "duly-made")]
    [InlineData("resume-during-assessment", "assessment-in-progress")]
    [InlineData("resume-during-decision", "awaiting-decision")]
    public void Reports_the_state_the_query_was_raised_from(
        string resumeActionId,
        string expectedOriginStateId
    )
    {
        var workItem = BuildUpdatedWorkItem(resumeActionId);

        var resolved = new ReAccreditationOriginStateResolver().ResolveOriginStateId(
            workItem,
            s_template
        );

        Assert.Equal(expectedOriginStateId, resolved);
    }

    /// <summary>
    /// Abstaining outside the waypoint keeps the resolver invisible: the
    /// engine falls back to the item's own state, so <c>OriginStateId</c>
    /// equals <c>StateId</c> for every ordinary application.
    /// </summary>
    [Theory]
    [InlineData("submitted")]
    [InlineData("duly-made")]
    [InlineData("assessment-in-progress")]
    [InlineData("awaiting-decision")]
    [InlineData("queried")]
    [InlineData("approved")]
    public void Abstains_for_any_state_other_than_the_waypoint(string stateId)
    {
        var workItem = BuildUpdatedWorkItem("resume-during-assessment");
        workItem.StateId = stateId;

        Assert.Null(
            new ReAccreditationOriginStateResolver().ResolveOriginStateId(workItem, s_template)
        );
    }

    [Fact]
    public void Abstains_for_a_work_item_of_another_type()
    {
        var workItem = BuildUpdatedWorkItem("resume-during-assessment");
        workItem = new WorkItem
        {
            TypeId = "some-other-type",
            StateId = "updated",
            SubmittedBy = workItem.SubmittedBy,
        };

        Assert.Null(
            new ReAccreditationOriginStateResolver().ResolveOriginStateId(workItem, s_template)
        );
    }

    /// <summary>
    /// An item whose frozen snapshot predates the continue-review-during-*
    /// transitions cannot have its origin derived. Abstaining makes the engine
    /// report <c>updated</c>, which correctly refuses every origin-specific
    /// call to action — far better than guessing a state and inviting a
    /// caseworker to send the application backwards past assessment.
    /// </summary>
    [Fact]
    public void Abstains_when_the_origin_cannot_be_derived()
    {
        var workItem = BuildUpdatedWorkItem(resumeActionId: null);

        Assert.Null(
            new ReAccreditationOriginStateResolver().ResolveOriginStateId(workItem, s_template)
        );
    }

    /// <summary>
    /// Only an entry that actually moved the item INTO <c>updated</c> counts.
    /// A synthetic action-applied entry stamped by a migration must not win
    /// the recency sort and mis-derive the origin.
    /// </summary>
    [Fact]
    public void Ignores_an_action_applied_entry_that_did_not_land_in_updated()
    {
        var workItem = BuildUpdatedWorkItem("resume-during-duly-making");
        workItem.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = "action-applied",
                ActionDisplayName = "Action applied",
                CreatedAt = DateTime.UtcNow,
                Details = new Dictionary<string, string?>
                {
                    ["actionId"] = "resume-during-decision",
                    ["fromStateId"] = "queried",
                    ["toStateId"] = "duly-made",
                },
            }
        );

        var resolved = new ReAccreditationOriginStateResolver().ResolveOriginStateId(
            workItem,
            s_template
        );

        Assert.Equal("submitted", resolved);
    }

    private static WorkItem BuildUpdatedWorkItem(string? resumeActionId)
    {
        var workItem = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "updated",
            SubmittedBy = "test-client",
        };

        if (resumeActionId is not null)
        {
            workItem.AuditLog.Add(
                new WorkItemAuditEntry
                {
                    Action = "action-applied",
                    ActionDisplayName = "Action applied",
                    CreatedAt = DateTime.UtcNow.AddHours(-1),
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

    private static readonly WorkItemTemplateSnapshot s_template = new()
    {
        TemplateVersion = "v12",
        States = [new WorkItemState("updated", "Updated")],
        Transitions =
        [
            new WorkItemTransition(
                "continue-review-during-duly-making", "Continue review", "updated", "submitted",
                CallerInvocable: false),
            new WorkItemTransition(
                "continue-review-during-duly-made", "Continue review", "updated", "duly-made",
                CallerInvocable: false),
            new WorkItemTransition(
                "continue-review-during-assessment", "Continue review", "updated",
                "assessment-in-progress", CallerInvocable: false),
            new WorkItemTransition(
                "continue-review-during-decision", "Continue review", "updated",
                "awaiting-decision", CallerInvocable: false),
        ],
    };
}
