using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

public class ReAccreditationTypeTests
{
    private readonly ReAccreditationType _type = new();

    [Fact]
    public void Declares_stable_identity_and_initial_state()
    {
        Assert.Equal("re-accreditation", _type.TypeId);
        Assert.Equal("Re-accreditation", _type.DisplayName);
        Assert.Equal("v4", _type.TemplateVersion);
        Assert.Equal("submitted", _type.InitialState.Id);
    }

    [Fact]
    public void States_include_terminal_approved_rejected_and_withdrawn()
    {
        var states = _type.States.ToDictionary(s => s.Id);

        Assert.True(states.ContainsKey("submitted"));
        Assert.True(states.ContainsKey("duly-made"));
        Assert.True(states.ContainsKey("assessment-in-progress"));
        Assert.True(states.ContainsKey("awaiting-decision"));
        Assert.True(states["approved"].IsTerminal);
        Assert.True(states["rejected"].IsTerminal);
        Assert.True(states["withdrawn"].IsTerminal);
        Assert.False(states["submitted"].IsTerminal);
        Assert.False(states["duly-made"].IsTerminal);
        Assert.False(states["assessment-in-progress"].IsTerminal);
        Assert.False(states["awaiting-decision"].IsTerminal);
    }

    [Theory]
    [InlineData("duly-make", "submitted", "duly-made", true)]
    [InlineData("payment-received", "duly-made", "assessment-in-progress", true)]
    [InlineData("sla-extend", "assessment-in-progress", "assessment-in-progress", false)]
    [InlineData("submit-for-decision", "assessment-in-progress", "awaiting-decision", true)]
    // RA-132: approve now fires directly from assessment-in-progress.
    [InlineData("approve", "assessment-in-progress", "approved", true)]
    [InlineData("reject", "awaiting-decision", "rejected", true)]
    [InlineData("withdraw", "submitted", "withdrawn", false)]
    [InlineData("withdraw-during-duly-made", "duly-made", "withdrawn", false)]
    [InlineData("withdraw-during-assessment", "assessment-in-progress", "withdrawn", false)]
    [InlineData("withdraw-during-decision", "awaiting-decision", "withdrawn", false)]
    public void Declares_expected_transition(
        string actionId, string fromStateId, string toStateId, bool requiresAllTasksComplete)
    {
        var transition = _type.Transitions.FirstOrDefault(t => t.ActionId == actionId);

        Assert.NotNull(transition);
        Assert.Equal(fromStateId, transition!.FromStateId);
        Assert.Equal(toStateId, transition.ToStateId);
        Assert.Equal(requiresAllTasksComplete, transition.RequiresAllTasksComplete);
    }

    [Theory]
    [InlineData("submitted", new[] { "verify-organisation-details", "confirm-application-completeness" })]
    [InlineData("duly-made", new[] { "confirm-registration-fee-paid" })]
    [InlineData("assessment-in-progress", new[]
    {
        "review-compliance-history", "assess-technical-capacity", "assess-financial-capacity"
    })]
    [InlineData("awaiting-decision", new[] { "record-decision-rationale" })]
    public void GetTasksForState_returns_expected_tasks(string stateId, string[] expectedTaskIds)
    {
        var tasks = _type.GetTasksForState(stateId);

        Assert.Equal(expectedTaskIds, tasks.Select(t => t.Id).ToArray());
        Assert.All(tasks, t => Assert.False(string.IsNullOrWhiteSpace(t.DisplayName)));
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    [InlineData("unknown-state")]
    public void Terminal_and_unknown_states_have_no_tasks(string stateId)
    {
        Assert.Empty(_type.GetTasksForState(stateId));
    }

    [Fact]
    public void Every_transition_references_declared_states()
    {
        var stateIds = _type.States.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var transition in _type.Transitions)
        {
            Assert.Contains(transition.FromStateId, stateIds);
            Assert.Contains(transition.ToStateId, stateIds);
        }
    }
}
