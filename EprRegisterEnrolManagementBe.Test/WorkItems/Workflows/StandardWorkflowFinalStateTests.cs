using EprRegisterEnrolManagementBe.WorkItems.Workflows;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Workflows;

/// <summary>
/// <see cref="StandardWorkflow.IsFinalState"/>: the closed states the Standard
/// lifecycle's own services refuse to act on.
/// </summary>
public sealed class StandardWorkflowFinalStateTests
{
    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    [InlineData("Approved")]
    public void Granted_refused_and_withdrawn_are_final(string stateId) =>
        Assert.True(StandardWorkflow.IsFinalState(stateId));

    [Theory]
    [InlineData("approved-pending-contact")]
    [InlineData("rejected-pending-contact")]
    [InlineData("awaiting-decision")]
    [InlineData("submitted")]
    [InlineData("")]
    [InlineData(null)]
    public void Every_other_state_is_not(string? stateId) =>
        Assert.False(StandardWorkflow.IsFinalState(stateId));

    [Fact]
    public void The_final_states_are_the_ones_the_workflow_declares_terminal() =>
        Assert.All(
            StandardWorkflow.Definition.States,
            state => Assert.Equal(state.IsTerminal, StandardWorkflow.IsFinalState(state.Id))
        );
}
