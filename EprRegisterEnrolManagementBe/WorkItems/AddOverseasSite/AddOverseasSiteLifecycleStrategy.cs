using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;

/// <summary>
/// RA-495: add-ORS's opt-in to the shared Standard-workflow lifecycle
/// services:
/// <list type="bullet">
///   <item>Push target: none yet, so nothing is pushed to the operator
///   journey (the change-request status route arrives with a later RA-495
///   task).</item>
///   <item>Minting: none. The site joins an accreditation that already
///   exists, so the determination writes are
///   <see cref="StandardDeterminationWrites"/>.</item>
///   <item>Notifications: none. The module registers no email hook.</item>
///   <item>Charge source: the site's own charge, <c>payload.site.chargePence</c>.</item>
/// </list>
/// The payment date is stamped as the default top-level <c>paymentDate</c>.
/// </summary>
internal sealed class AddOverseasSiteLifecycleStrategy(StandardDeterminationWrites determinations)
    : IStandardLifecycleStrategy
{
    private static readonly AddOverseasSiteType s_type = new();

    public IWorkItemType Type => s_type;

    public IOperatorJourneyPush? OperatorJourneyPush => null;

    public IStandardDeterminationSteps Determinations => determinations;

    public int? ReadChargePence(WorkItem workItem) =>
        workItem.Payload is { } payload
        && payload.TryGetValue("site", out var site)
        && site.IsBsonDocument
        && site.AsBsonDocument.TryGetValue("chargePence", out var charge)
        && BsonWholeNumber.TryGet(charge, 0, int.MaxValue, out var pence)
            ? pence
            : null;
}
