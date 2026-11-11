using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.Workflows;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Workflows;

/// <summary>
/// RA-739: the Standard workflow is defined once and shared by every case
/// type that follows it (re-accreditation now, add-ORS next).
/// </summary>
public class StandardWorkflowTests
{
    private readonly WorkItemWorkflow _workflow = StandardWorkflow.Definition;

    [Fact]
    public void Starts_in_submitted_shown_as_not_started()
    {
        Assert.Equal("standard", _workflow.Id);
        Assert.Equal("submitted", _workflow.InitialState.Id);
        Assert.Equal("Not started", _workflow.InitialState.DisplayName);
    }

    [Fact]
    public void Every_transition_moves_between_declared_states()
    {
        var stateIds = _workflow.States.Select(s => s.Id).ToHashSet();

        Assert.All(
            _workflow.Transitions,
            t =>
            {
                Assert.Contains(t.FromStateId, stateIds);
                Assert.Contains(t.ToStateId, stateIds);
            }
        );
    }

    [Fact]
    public void Declares_the_standard_states_in_status_sort_order()
    {
        Assert.Equal(
            [
                "submitted",
                "duly-made",
                "assessment-started",
                "assessment-in-progress",
                "awaiting-decision",
                "queried",
                "updated",
                "approved-pending-contact",
                "approved",
                "rejected-pending-contact",
                "rejected",
                "withdrawn",
            ],
            _workflow.States.Select(s => s.Id)
        );
    }

    [Fact]
    public void Can_be_referenced_by_more_than_one_type()
    {
        IWorkItemType reAccreditation = new ReAccreditationType();
        IWorkItemType other = new OtherStandardType();

        var registry = new WorkItemRegistry([reAccreditation, other]);

        Assert.Equal(2, registry.Types.Count);
        Assert.Same(reAccreditation.Workflow, other.Workflow);
        Assert.Equal(reAccreditation.InitialState, other.InitialState);
        Assert.Equal(reAccreditation.States, other.States);
        Assert.Equal(reAccreditation.Transitions, other.Transitions);
    }

    private sealed class OtherStandardType : IWorkItemType
    {
        public string TypeId => "other-standard";
        public string DisplayName => "Other standard";
        public WorkItemWorkflow Workflow => StandardWorkflow.Definition;
        public WorkItemState InitialState => Workflow.InitialState;
        public IReadOnlyCollection<WorkItemState> States => Workflow.States;
        public IReadOnlyCollection<WorkItemTransition> Transitions => Workflow.Transitions;
    }
}