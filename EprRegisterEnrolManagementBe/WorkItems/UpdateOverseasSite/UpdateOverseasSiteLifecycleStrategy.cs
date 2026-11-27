using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

namespace EprRegisterEnrolManagementBe.WorkItems.UpdateOverseasSite;

/// <summary>
/// RA-682: the ORS update's opt-in to the shared Standard-workflow lifecycle,
/// the same as add-ORS's (<see cref="AddOverseasSite.AddOverseasSiteLifecycleStrategy"/>)
/// apart from the charge:
/// <list type="bullet">
///   <item>Push target: the operator journey's change-request status route, so
///   the decision and its effective date go back to the operator backend,
///   which applies the change.</item>
///   <item>Minting: none. The determination writes are
///   <see cref="StandardDeterminationWrites"/>.</item>
///   <item>Notifications: none.</item>
///   <item>Charge: always 0 (AC05).</item>
/// </list>
/// </summary>
internal sealed class UpdateOverseasSiteLifecycleStrategy(StandardDeterminationWrites determinations)
    : IStandardLifecycleStrategy
{
    private static readonly UpdateOverseasSiteType s_type = new();

    public IWorkItemType Type => s_type;

    public IOperatorJourneyPush? OperatorJourneyPush => OperatorJourneyRoutePush.ChangeRequest;

    public IStandardDeterminationSteps Determinations => determinations;

    /// <summary>An approval names the date the new details take effect from (AC07).</summary>
    public bool TakesEffectiveDate => true;

    /// <summary>RA-682 AC05: an ORS update is never charged.</summary>
    public int? ReadChargePence(WorkItem workItem) => 0;
}
