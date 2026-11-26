using EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.Workflows;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.AddOverseasSite;

public sealed class AddOverseasSiteTypeTests
{
    private static bool IsWithdraw(WorkItemTransition t) => t.ActionId.StartsWith("withdraw", StringComparison.Ordinal);

    // RA-739: the add-ORS case type configuration.
    [Fact]
    public void Declares_its_case_type_configuration()
    {
        IWorkItemType type = new AddOverseasSiteType();

        Assert.Equal("add-overseas-site", type.TypeId);
        Assert.Equal("New ORS accreditation", type.DisplayName);
        Assert.Equal(WorkItemCategories.InYearChange, type.Category);
        Assert.Equal("ORS", type.InYearChangeCode);
        Assert.True(type.SplitsSubmissionPerSite);
        Assert.Equal("v3", type.TemplateVersion);
        Assert.Equal("submitted", type.InitialState.Id);
    }

    // RA-495 review: the Standard workflow's states and transitions, except
    // that no withdraw* declaration is caller-invocable.
    [Fact]
    public void Follows_the_standard_workflow_with_every_withdraw_transition_not_caller_invocable()
    {
        IWorkItemType type = new AddOverseasSiteType();

        Assert.Same(StandardWorkflow.Definition.InitialState, type.Workflow.InitialState);
        Assert.Same(StandardWorkflow.Definition.States, type.Workflow.States);

        var withdraws = type.Transitions.Where(IsWithdraw).ToList();
        Assert.Equal(7, withdraws.Count);
        Assert.All(withdraws, t => Assert.False(t.CallerInvocable));
        Assert.Equal(
            StandardWorkflow.Definition.Transitions.Select(t => IsWithdraw(t) ? t with { CallerInvocable = false } : t),
            type.Transitions
        );
    }

    // RA-495 review: re-accreditation still withdraws through the shared
    // Standard workflow, which is unchanged.
    [Fact]
    public void Leaves_re_accreditation_withdraw_caller_invocable()
    {
        var withdraws = new ReAccreditationType().Transitions.Where(IsWithdraw).ToList();

        Assert.Equal(7, withdraws.Count);
        Assert.All(withdraws, t => Assert.True(t.CallerInvocable));
    }

    // RA-698 (v3): the query, resume and continue-review transitions are the
    // Standard workflow's, reachable only through the module's bespoke services.
    [Fact]
    public void Declares_the_query_resume_and_continue_review_transitions_as_bespoke_only()
    {
        var bespoke = new AddOverseasSiteType()
            .Transitions.Where(t =>
                t.ActionId.StartsWith("query-during-", StringComparison.Ordinal)
                || t.ActionId.StartsWith("resume-during-", StringComparison.Ordinal)
                || t.ActionId.StartsWith("continue-review-during-", StringComparison.Ordinal)
            )
            .ToList();

        // 5 query (assessment-started shares query-during-assessment), 4 resume, 4 continue-review.
        Assert.Equal(13, bespoke.Count);
        Assert.All(bespoke, t => Assert.False(t.CallerInvocable));
    }

    [Fact]
    public void Registers_alongside_re_accreditation_on_the_shared_standard_workflow()
    {
        var registry = new WorkItemRegistry([new ReAccreditationType(), new AddOverseasSiteType()]);

        Assert.NotNull(registry.Find("add-overseas-site"));
        Assert.NotNull(registry.Find("re-accreditation"));
    }
}
