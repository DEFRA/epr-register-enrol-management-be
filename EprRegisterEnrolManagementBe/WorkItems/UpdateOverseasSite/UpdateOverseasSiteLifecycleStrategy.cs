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
///   <item>Notifications: none.</item>
///   <item>Charge: always 0 (AC05), so duly making takes no payment date.</item>
///   <item>Valid-from: none. The operator backend applies an update when it
///   is told of the approval, so the decision takes no date and the push
///   carries none.</item>
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
    /// An update has no valid-from date: it takes effect when the operator
    /// backend applies it, so neither the decision nor change-valid-from takes one.
    /// </summary>
    public bool TakesEffectiveDate => false;

    /// <summary>RA-682 AC05: an ORS update is never charged.</summary>
    public int? ReadChargePence(WorkItem workItem) => 0;

    /// <summary>
    /// Nothing is paid, so there is no payment date to enter; the SLA runs from
    /// when the update is duly made.
    /// </summary>
    public bool TakesPaymentDate => false;
}
