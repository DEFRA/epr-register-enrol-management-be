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
        Assert.Equal("v12", _type.TemplateVersion);
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
    [InlineData("payment-received", "duly-made", "assessment-in-progress")]
    [InlineData("sla-extend", "assessment-in-progress", "assessment-in-progress")]
    [InlineData("submit-for-decision", "assessment-in-progress", "awaiting-decision")]
    // RA-132: approve is NOT a generic-engine transition; it is handled
    // exclusively by ReAccreditationApprovalService.
    [InlineData("reject", "awaiting-decision", "rejected")]
    // RA-291: query is available from every pre-decision state.
    [InlineData("query-during-duly-making", "submitted", "queried")]
    [InlineData("query-during-duly-made", "duly-made", "queried")]
    [InlineData("query-during-assessment", "assessment-in-progress", "queried")]
    [InlineData("query-during-decision", "awaiting-decision", "queried")]
    // RA-311/MBE-1: the inverse of the four query-during-* transitions above.
    // RA-337: these land on 'updated', not the originating state directly.
    [InlineData("resume-during-duly-making", "queried", "updated")]
    [InlineData("resume-during-duly-made", "queried", "updated")]
    [InlineData("resume-during-assessment", "queried", "updated")]
    [InlineData("resume-during-decision", "queried", "updated")]
    // RA-337: the inverse of the four resume-during-* transitions above.
    [InlineData("continue-review-during-duly-making", "updated", "submitted")]
    [InlineData("continue-review-during-duly-made", "updated", "duly-made")]
    [InlineData("continue-review-during-assessment", "updated", "assessment-in-progress")]
    [InlineData("continue-review-during-decision", "updated", "awaiting-decision")]
    [InlineData("withdraw", "submitted", "withdrawn")]
    [InlineData("withdraw-during-duly-made", "duly-made", "withdrawn")]
    [InlineData("withdraw-during-assessment", "assessment-in-progress", "withdrawn")]
    [InlineData("withdraw-during-decision", "awaiting-decision", "withdrawn")]
    [InlineData("withdraw-during-query", "queried", "withdrawn")]
    [InlineData("withdraw-during-updated", "updated", "withdrawn")]
    public void Declares_expected_transition(
        string actionId,
        string fromStateId,
        string toStateId
    )
    {
        var transition = _type.Transitions.FirstOrDefault(t => t.ActionId == actionId);

        Assert.NotNull(transition);
        Assert.Equal(fromStateId, transition!.FromStateId);
        Assert.Equal(toStateId, transition.ToStateId);
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
