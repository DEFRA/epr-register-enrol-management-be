using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-523: the declared shape of <c>payment-received-during-duly-made</c>.
///
/// These are contract assertions rather than behaviour tests. Three consumers
/// outside this repo key off the values below — management-fe mirrors the
/// transition to render its call to action, mgmt-tests asserts on the state
/// ids, and the operator backend receives the resulting status push — so a
/// silent edit to any of them is a cross-repo break rather than a local
/// refactor.
/// </summary>
public class ReAccreditationPaymentReceivedContractTests
{
    private const string ActionId = "payment-received-during-duly-made";

    private static readonly ReAccreditationType s_type = new();

    private static WorkItemTransition NewTransition() =>
        Assert.Single(s_type.Transitions, t => t.ActionId == ActionId);

    [Fact]
    public void Transition_is_declared_with_the_agreed_shape()
    {
        var transition = NewTransition();

        Assert.Equal("updated", transition.FromStateId);
        Assert.Equal("assessment-in-progress", transition.ToStateId);
        Assert.Equal("Start assessment", transition.DisplayName);
    }

    /// <summary>
    /// The security boundary. This transition shares FromStateId 'updated' with
    /// the four continue-review-during-* transitions, so the engine's
    /// from-state guard cannot tell them apart. Were it caller-invocable, a
    /// caller holding a 'submitted'-origin item in 'updated' could drive it
    /// through the generic action endpoint and skip duly making — capturing no
    /// payment date and therefore never starting the SLA clock.
    /// </summary>
    [Fact]
    public void Transition_is_not_caller_invocable()
    {
        Assert.False(NewTransition().CallerInvocable);
    }

    /// <summary>
    /// Every transition out of the 'updated' waypoint that can be confused with
    /// this one must be non-invocable too, or the guard above is pointless: a
    /// caller could pick a sibling instead. withdraw-during-updated is the
    /// deliberate exception — it has exactly one possible destination, so there
    /// is no ambiguity for the from-state guard to resolve.
    /// </summary>
    [Fact]
    public void Every_ambiguous_transition_out_of_updated_is_non_invocable()
    {
        var ambiguous = s_type
            .Transitions.Where(t => t.FromStateId == "updated" && t.ActionId != "withdraw-during-updated")
            .ToList();

        Assert.NotEmpty(ambiguous);
        Assert.All(ambiguous, t => Assert.False(t.CallerInvocable));
    }

    /// <summary>
    /// Retained on purpose.
    /// <see cref="ReAccreditationUpdatedOrigin.ResolveOriginatingStateId"/>
    /// derives an item's origin by looking this transition's ToStateId up in
    /// the item's own snapshot, so removing it would make the origin
    /// unresolvable for exactly the items RA-523 exists for — the service would
    /// refuse them and OriginStateId would go null, costing management-fe the
    /// discriminator it renders the new call to action from. It is no longer
    /// OFFERED, which is a frontend concern; it is not removed.
    /// </summary>
    [Fact]
    public void Continue_review_during_duly_made_is_retained()
    {
        var retained = Assert.Single(
            s_type.Transitions, t => t.ActionId == "continue-review-during-duly-made");

        Assert.Equal("updated", retained.FromStateId);
        Assert.Equal("duly-made", retained.ToStateId);
    }

    [Fact]
    public void Template_version_is_bumped_for_the_new_transition()
    {
        Assert.Equal("v14", s_type.TemplateVersion);
    }

    /// <summary>
    /// The id must stay distinct from 'payment-received'. Both
    /// ReAccreditationNotificationHook.s_actionTemplates and
    /// ReAccreditationStatusPushHook.BuildExcludedActionIds key on action id, so
    /// a shared id would make one id carry two CallerInvocable postures and two
    /// notification meanings.
    /// </summary>
    [Fact]
    public void Sibling_payment_received_is_unchanged_and_still_invocable()
    {
        var sibling = Assert.Single(s_type.Transitions, t => t.ActionId == "payment-received");

        Assert.Equal("duly-made", sibling.FromStateId);
        Assert.Equal("assessment-in-progress", sibling.ToStateId);
        Assert.True(sibling.CallerInvocable);
        Assert.NotEqual(ActionId, sibling.ActionId);
    }

    /// <summary>
    /// ReAccreditationStatusPushHook excludes an action only when its transition
    /// MOVES an item onto 'queried' or 'withdrawn'. This one lands on
    /// 'assessment-in-progress' and is not a self-loop, so the operator backend
    /// must receive the status push — asserted here rather than assumed,
    /// because a missing push would make the operator's view depend on which
    /// route the caseworker happened to take.
    /// </summary>
    [Fact]
    public void Transition_qualifies_for_the_operator_status_push()
    {
        var transition = NewTransition();

        Assert.NotEqual(transition.FromStateId, transition.ToStateId);
        Assert.NotEqual("queried", transition.ToStateId);
        Assert.NotEqual("withdrawn", transition.ToStateId);
    }
}
