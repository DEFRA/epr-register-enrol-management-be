using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-000 review: the one transition lookup the engine and the generic action
/// endpoint's caller-invocable guard share.
/// </summary>
public class WorkItemEngineRulesTests
{
    private static readonly IWorkItemTemplate s_template =
        WorkItemTemplateSnapshot.Capture(new ReAccreditationType());

    [Theory]
    [InlineData("assessment-in-progress", "sla-extend", "assessment-in-progress")]
    [InlineData("queried", "sla-extend", "queried")]
    [InlineData("Queried", "SLA-Extend", "queried")]
    public void ResolveTransition_picks_the_declaration_for_the_current_state(
        string stateId,
        string actionId,
        string expectedFromStateId
    )
    {
        var transition = WorkItemEngineRules.ResolveTransition(s_template, stateId, actionId);

        Assert.NotNull(transition);
        Assert.Equal(expectedFromStateId, transition!.FromStateId);
        Assert.Equal("sla-extend", transition.ActionId);
    }

    [Fact]
    public void ResolveTransition_falls_back_to_the_first_declaration_when_no_from_state_matches()
    {
        var transition = WorkItemEngineRules.ResolveTransition(s_template, "approved", "sla-extend");

        Assert.Same(s_template.Transitions.First(t => t.ActionId == "sla-extend"), transition);
    }

    [Fact]
    public void ResolveTransition_returns_null_for_an_undeclared_action()
    {
        Assert.Null(WorkItemEngineRules.ResolveTransition(s_template, "submitted", "not-an-action"));
    }
}
