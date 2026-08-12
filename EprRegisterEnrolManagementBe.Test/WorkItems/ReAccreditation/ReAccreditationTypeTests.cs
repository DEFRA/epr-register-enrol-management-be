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
        Assert.Equal("v11", _type.TemplateVersion);
        Assert.Equal("submitted", _type.InitialState.Id);
    }

    // RA-324/AC06: state DisplayNames align with the "Applications" design.
    // Only the labels changed; the ids are the wire contract and stay put.
    [Theory]
    [InlineData("submitted", "Not started")]
    [InlineData("assessment-in-progress", "Updated")]
    [InlineData("approved", "Granted")]
    [InlineData("rejected", "Refused")]
    // Untouched labels — proves the rename did not spill over.
    [InlineData("duly-made", "Duly made")]
    [InlineData("awaiting-decision", "Awaiting decision")]
    [InlineData("queried", "Queried")]
    [InlineData("updated", "Updated")]
    [InlineData("withdrawn", "Withdrawn")]
    public void States_declare_expected_display_names(string stateId, string expectedDisplayName)
    {
        var state = _type.States.Single(s => s.Id == stateId);

        Assert.Equal(expectedDisplayName, state.DisplayName);
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
        Assert.True(states.ContainsKey("queried"));
        Assert.False(states["queried"].IsTerminal);
        Assert.True(states.ContainsKey("updated"));
        Assert.False(states["updated"].IsTerminal);
    }

    [Theory]
    [InlineData("payment-received", "duly-made", "assessment-in-progress", true)]
    [InlineData("sla-extend", "assessment-in-progress", "assessment-in-progress", false)]
    [InlineData("submit-for-decision", "assessment-in-progress", "awaiting-decision", true)]
    // RA-132: approve is NOT a generic-engine transition; it is handled
    // exclusively by ReAccreditationApprovalService.
    [InlineData("reject", "awaiting-decision", "rejected", true)]
    // RA-291: query is available from every pre-decision state.
    [InlineData("query-during-duly-making", "submitted", "queried", false)]
    [InlineData("query-during-duly-made", "duly-made", "queried", false)]
    [InlineData("query-during-assessment", "assessment-in-progress", "queried", false)]
    [InlineData("query-during-decision", "awaiting-decision", "queried", false)]
    // RA-311/MBE-1: the inverse of the four query-during-* transitions above.
    // RA-337: these land on 'updated', not the originating state directly.
    [InlineData("resume-during-duly-making", "queried", "updated", false)]
    [InlineData("resume-during-duly-made", "queried", "updated", false)]
    [InlineData("resume-during-assessment", "queried", "updated", false)]
    [InlineData("resume-during-decision", "queried", "updated", false)]
    // RA-337: the inverse of the four resume-during-* transitions above.
    [InlineData("continue-review-during-duly-making", "updated", "submitted", false)]
    [InlineData("continue-review-during-duly-made", "updated", "duly-made", false)]
    [InlineData("continue-review-during-assessment", "updated", "assessment-in-progress", false)]
    [InlineData("continue-review-during-decision", "updated", "awaiting-decision", false)]
    [InlineData("withdraw", "submitted", "withdrawn", false)]
    [InlineData("withdraw-during-duly-made", "duly-made", "withdrawn", false)]
    [InlineData("withdraw-during-assessment", "assessment-in-progress", "withdrawn", false)]
    [InlineData("withdraw-during-decision", "awaiting-decision", "withdrawn", false)]
    [InlineData("withdraw-during-query", "queried", "withdrawn", false)]
    [InlineData("withdraw-during-updated", "updated", "withdrawn", false)]
    public void Declares_expected_transition(
        string actionId,
        string fromStateId,
        string toStateId,
        bool requiresAllTasksComplete
    )
    {
        var transition = _type.Transitions.FirstOrDefault(t => t.ActionId == actionId);

        Assert.NotNull(transition);
        Assert.Equal(fromStateId, transition!.FromStateId);
        Assert.Equal(toStateId, transition.ToStateId);
        Assert.Equal(requiresAllTasksComplete, transition.RequiresAllTasksComplete);
    }

    [Theory]
    // RA-316: 'submitted' declares no tasks. Duly making is an explicit call to
    // action carrying a payment date, so the two tasks that used to drive the
    // auto-transition out of this state were deleted with the hook.
    [InlineData("submitted", new string[0])]
    [InlineData("duly-made", new[] { "confirm-registration-fee-paid" })]
    [InlineData(
        "assessment-in-progress",
        new[]
        {
            "review-compliance-history",
            "assess-technical-capacity",
            "assess-financial-capacity",
        }
    )]
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
