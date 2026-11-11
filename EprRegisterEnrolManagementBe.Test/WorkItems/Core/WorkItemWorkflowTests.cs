using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>RA-739: a workflow is a reusable, self-consistent state machine.</summary>
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

    [Fact]
    public void Rejects_an_initial_state_it_does_not_declare()
    {
        Assert.Throws<InvalidWorkItemTypeException>(() =>
            new WorkItemWorkflow("broken", s_open, [s_closed], [])
        );
    }

    [Theory]
    [InlineData("missing", "closed")]
    [InlineData("open", "missing")]
    public void Rejects_a_transition_between_undeclared_states(string from, string to)
    {
        Assert.Throws<InvalidWorkItemTypeException>(() =>
            new WorkItemWorkflow(
                "broken",
                s_open,
                [s_open, s_closed],
                [new WorkItemTransition("move", "Move", from, to)]
            )
        );
    }
}