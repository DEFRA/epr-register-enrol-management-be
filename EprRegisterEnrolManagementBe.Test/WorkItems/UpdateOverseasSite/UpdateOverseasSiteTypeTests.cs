using EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.UpdateOverseasSite;
using EprRegisterEnrolManagementBe.WorkItems.Workflows;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.UpdateOverseasSite;

public sealed class UpdateOverseasSiteTypeTests
{
    private static bool IsWithdraw(WorkItemTransition t) => t.ActionId.StartsWith("withdraw", StringComparison.Ordinal);

    // RA-682: the ORS update case type configuration. It shares add-ORS's
    // in-year change code (one IYC-ORS series) and is never charged.
    [Fact]
    public void Declares_its_case_type_configuration()
    {
        IWorkItemType type = new UpdateOverseasSiteType();

        Assert.Equal("update-overseas-site", type.TypeId);
        Assert.Equal("ORS update", type.DisplayName);
        Assert.Equal(WorkItemCategories.InYearChange, type.Category);
        Assert.Equal("ORS", type.InYearChangeCode);
        Assert.True(type.SplitsSubmissionPerSite);
        Assert.False(type.RequiresPaymentReference);
        Assert.Equal("v1", type.TemplateVersion);
        Assert.Equal("submitted", type.InitialState.Id);
    }

    // RA-682: add-ORS and every other type still need a payment reference.
    [Fact]
    public void Add_ORS_still_requires_a_payment_reference()
    {
        Assert.True(((IWorkItemType)new AddOverseasSiteType()).RequiresPaymentReference);
        Assert.True(((IWorkItemType)new ReAccreditationType()).RequiresPaymentReference);
    }

    // RA-682: the same workflow as add-ORS, under its own id.
    [Fact]
    public void Follows_the_standard_workflow_with_every_withdraw_transition_not_caller_invocable()
    {
        IWorkItemType type = new UpdateOverseasSiteType();

        Assert.Equal("standard-update-overseas-site", type.Workflow.Id);
        Assert.Same(StandardWorkflow.Definition.InitialState, type.Workflow.InitialState);
        Assert.Same(StandardWorkflow.Definition.States, type.Workflow.States);
        var withdraws = type.Transitions.Where(IsWithdraw).ToList();
        Assert.Equal(7, withdraws.Count);
        Assert.All(withdraws, t => Assert.False(t.CallerInvocable));
        Assert.Equal(AddOverseasSiteType.WorkflowDefinition.Transitions, type.Transitions);
    }

    [Fact]
    public void Registers_alongside_add_ORS_and_re_accreditation()
    {
        var registry = new WorkItemRegistry(
            [new ReAccreditationType(), new AddOverseasSiteType(), new UpdateOverseasSiteType()]
        );

        Assert.NotNull(registry.Find("update-overseas-site"));
        Assert.NotNull(registry.Find("add-overseas-site"));
        Assert.NotNull(registry.Find("re-accreditation"));
    }
}
