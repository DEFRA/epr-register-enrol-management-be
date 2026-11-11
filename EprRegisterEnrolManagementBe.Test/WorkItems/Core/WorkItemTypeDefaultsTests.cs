using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-739: the case type configuration a type does not declare defaults to
/// "no category, no submission reference, its own states as the workflow".
/// </summary>
public class WorkItemTypeDefaultsTests
{
    private static readonly WorkItemState s_open = new("open", "Open");
    private static readonly WorkItemState s_closed = new("closed", "Closed", IsTerminal: true);
    private static readonly WorkItemTransition s_close = new("close", "Close", "open", "closed");

    [Fact]
    public void A_type_that_declares_nothing_new_has_no_category_and_no_submission_reference()
    {
        IWorkItemType type = new BespokeType();

        Assert.Null(type.Category);
        Assert.False(type.UsesSubmissionReference);
        Assert.Null(type.InYearChangeCode);
    }

    [Fact]
    public void A_type_with_its_own_states_has_a_bespoke_workflow_built_from_them()
    {
        IWorkItemType type = new BespokeType();

        Assert.Equal("bespoke", type.Workflow.Id);
        Assert.Equal(s_open, type.Workflow.InitialState);
        Assert.Equal([s_open, s_closed], type.Workflow.States);
        Assert.Equal([s_close], type.Workflow.Transitions);
    }

    [Fact]
    public void In_year_change_category_is_in_year_change()
    {
        Assert.Equal("in-year-change", WorkItemCategories.InYearChange);
    }

    private sealed class BespokeType : IWorkItemType
    {
        public string TypeId => "bespoke";
        public string DisplayName => "Bespoke";
        public WorkItemState InitialState => s_open;
        public IReadOnlyCollection<WorkItemState> States => [s_open, s_closed];
        public IReadOnlyCollection<WorkItemTransition> Transitions => [s_close];
    }
}