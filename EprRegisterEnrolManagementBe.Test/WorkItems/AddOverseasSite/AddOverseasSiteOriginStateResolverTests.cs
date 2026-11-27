using EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.AddOverseasSite;

/// <summary>RA-698 review: an updated add-ORS item reports the state it was queried from.</summary>
public class AddOverseasSiteOriginStateResolverTests
{
    private static readonly WorkItemTemplateSnapshot s_template = new()
    {
        TemplateVersion = "v3",
        States = [new WorkItemState("updated", "Updated")],
        Transitions =
        [
            new WorkItemTransition("continue-review-during-duly-making", "Continue review", "updated", "submitted", CallerInvocable: false),
            new WorkItemTransition("continue-review-during-duly-made", "Continue review", "updated", "duly-made", CallerInvocable: false),
            new WorkItemTransition("continue-review-during-assessment", "Continue review", "updated", "assessment-in-progress", CallerInvocable: false),
        ],
    };

    private static WorkItem Updated(string typeId, string stateId, string? resumeActionId)
    {
        var item = new WorkItem { TypeId = typeId, StateId = stateId, SubmittedBy = "test-client" };
        if (resumeActionId is not null)
        {
            item.AuditLog.Add(
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

        return item;
    }

    [Fact]
    public void Is_registered_for_the_add_overseas_site_type() =>
        Assert.Equal(AddOverseasSiteType.Id, new AddOverseasSiteOriginStateResolver().TypeId);

    [Theory]
    [InlineData("resume-during-duly-making", "submitted")]
    [InlineData("resume-during-duly-made", "duly-made")]
    [InlineData("resume-during-assessment", "assessment-in-progress")]
    public void Reports_the_state_the_query_was_raised_from(string resumeActionId, string expected) =>
        Assert.Equal(
            expected,
            new AddOverseasSiteOriginStateResolver().ResolveOriginStateId(
                Updated(AddOverseasSiteType.Id, "updated", resumeActionId),
                s_template
            )
        );

    [Theory]
    [InlineData(AddOverseasSiteType.Id, "queried")]
    [InlineData(AddOverseasSiteType.Id, "submitted")]
    [InlineData("some-other-type", "updated")]
    public void Abstains_outside_the_updated_waypoint_and_for_other_types(string typeId, string stateId) =>
        Assert.Null(
            new AddOverseasSiteOriginStateResolver().ResolveOriginStateId(
                Updated(typeId, stateId, "resume-during-assessment"),
                s_template
            )
        );

    [Fact]
    public void Abstains_when_the_origin_cannot_be_derived() =>
        Assert.Null(
            new AddOverseasSiteOriginStateResolver().ResolveOriginStateId(
                Updated(AddOverseasSiteType.Id, "updated", null),
                s_template
            )
        );

    [Fact]
    public void Refuses_null_arguments()
    {
        var resolver = new AddOverseasSiteOriginStateResolver();
        Assert.Throws<ArgumentNullException>(() => resolver.ResolveOriginStateId(null!, s_template));
        Assert.Throws<ArgumentNullException>(() =>
            resolver.ResolveOriginStateId(Updated(AddOverseasSiteType.Id, "updated", null), null!)
        );
    }
}
