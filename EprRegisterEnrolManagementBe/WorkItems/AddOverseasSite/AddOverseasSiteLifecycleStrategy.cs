using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;

/// <summary>
/// RA-495: add-ORS's opt-in to the shared Standard-workflow lifecycle
/// services:
/// <list type="bullet">
///   <item>Push target: the operator journey's change-request status route
///   (<see cref="OperatorJourneyRoutePush.ChangeRequest"/>). Transitions are
///   pushed after they commit, with re-accreditation's skips, and confirm
///   contact pushes the final decision before it commits.</item>
///   <item>Minting: none. The site joins an accreditation that already
///   exists, so the determination writes are
///   <see cref="StandardDeterminationWrites"/>.</item>
///   <item>Notifications: the post-commit status push
///   (<see cref="StandardStatusPushHook"/>) only. The module registers no
///   GOV.UK Notify email hook.</item>
///   <item>Charge source: the site's own charge, <c>payload.site.chargePence</c>.</item>
/// </list>
/// The payment date is stamped as the default top-level <c>paymentDate</c>.
/// A determination records an effective date (AC6) on the top-level
/// <c>effectiveDate</c>: the one entered on approval, the submission date on
/// refusal.
/// </summary>
internal sealed class AddOverseasSiteLifecycleStrategy(StandardDeterminationWrites determinations)
    : IStandardLifecycleStrategy
{
    private static readonly AddOverseasSiteType s_type = new();

    /// <summary>The site's charge, in pence, on <see cref="SubmissionPayload.SiteField"/>.</summary>
    private const string SiteChargePenceField = "chargePence";

    public IWorkItemType Type => s_type;

    public IOperatorJourneyPush? OperatorJourneyPush => OperatorJourneyRoutePush.ChangeRequest;

    public IStandardDeterminationSteps Determinations => determinations;

    /// <summary>RA-495 AC6: an approval names its effective date, and the site is accredited from it.</summary>
    public bool TakesEffectiveDate => true;

    public int? ReadChargePence(WorkItem workItem) =>
        SubmissionPayload.ReadSite(workItem.Payload) is { } site
        && site.TryGetValue(SiteChargePenceField, out var charge)
        && BsonWholeNumber.TryGet(charge, 0, int.MaxValue, out var pence)
            ? pence
            : null;
}
