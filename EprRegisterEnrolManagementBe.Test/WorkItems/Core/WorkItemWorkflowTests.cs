using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>RA-739: a workflow is a reusable state machine.</summary>
public class WorkItemWorkflowTests
{
    private static readonly WorkItemState s_open = new("open", "Open");
    private static readonly WorkItemState s_closed = new("closed", "Closed", IsTerminal: true);

    [Fact]
    public void Exposes_its_states_transitions_and_initial_state()
    {
        var close = new WorkItemTransition("close", "Close", "open", "closed");

        var workflow = new WorkItemWorkflow("simple", s_open, [s_open, s_closed], [close]);

        Assert.Equal("simple", workflow.Id);
        Assert.Equal(s_open, workflow.InitialState);
        Assert.Equal([s_open, s_closed], workflow.States);
        Assert.Equal([close], workflow.Transitions);
    }
}