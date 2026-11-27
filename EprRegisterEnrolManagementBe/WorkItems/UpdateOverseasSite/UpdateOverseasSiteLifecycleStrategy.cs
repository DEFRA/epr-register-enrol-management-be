using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

namespace EprRegisterEnrolManagementBe.WorkItems.UpdateOverseasSite;

/// <summary>
/// RA-682: the ORS update's opt-in to the shared Standard-workflow lifecycle,
/// the same as add-ORS's (<see cref="AddOverseasSite.AddOverseasSiteLifecycleStrategy"/>)
/// apart from the charge and the dates:
/// <list type="bullet">
///   <item>Push target: the operator journey's change-request status route, so
///   the decision goes back to the operator backend, which applies the
///   change.</item>
///   <item>Minting: none. The determination writes are
///   <see cref="StandardDeterminationWrites"/>.</item>
///   <item>Notifications: the same as add-ORS's, the post-commit status push
///   (<see cref="StandardStatusPushHook"/>, registered by
///   <c>AddStandardLifecycle</c>). Neither type has a GOV.UK Notify email
///   hook yet: <c>ReAccreditationNotificationHook</c> sends
///   re-accreditation's only.</item>
///   <item>Charge: always 0 (AC05), so duly making takes no payment date.</item>
///   <item>Valid-from: an approval takes effect from the UK date it was
///   determined (RA-782 AC6, <see cref="AutomaticEffectiveDate"/>). The
///   caseworker enters no date, so the decision takes none and
///   change-valid-from is refused; the confirm-contact push carries the
///   determination date, and a refusal's carries none.</item>
/// </list>
/// </summary>
internal sealed class UpdateOverseasSiteLifecycleStrategy(StandardDeterminationWrites determinations)
    : IStandardLifecycleStrategy
{
    private static readonly UpdateOverseasSiteType s_type = new();

    public IWorkItemType Type => s_type;

    public IOperatorJourneyPush? OperatorJourneyPush => OperatorJourneyRoutePush.ChangeRequest;

    public IStandardDeterminationSteps Determinations => determinations;

    /// <summary>
    /// No caseworker-entered valid-from date: neither the decision nor
    /// change-valid-from takes one (see <see cref="AutomaticEffectiveDate"/>).
    /// </summary>
    public bool TakesEffectiveDate => false;

    /// <summary>RA-782 AC6: an approved update takes effect from the date it was determined.</summary>
    public DateOnly? AutomaticEffectiveDate(WorkItem workItem, StandardDecisionOutcome outcome) =>
        outcome == StandardDecisionOutcome.Approved ? StandardEffectiveDate.OfApprovedDetermination(workItem) : null;

    /// <summary>RA-682 AC05: an ORS update is never charged.</summary>
    public int? ReadChargePence(WorkItem workItem) => 0;

    /// <summary>
    /// Nothing is paid, so there is no payment date to enter; the SLA runs from
    /// when the update is duly made.
    /// </summary>
    public bool TakesPaymentDate => false;
}
