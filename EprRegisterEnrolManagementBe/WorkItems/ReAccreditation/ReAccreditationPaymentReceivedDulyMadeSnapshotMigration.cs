using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Adds the <c>updated → assessment-in-progress</c>
/// <c>payment-received-during-duly-made</c> transition (RA-523) to the frozen
/// <see cref="WorkItemTemplateSnapshot"/> of every re-accreditation work item
/// and bumps <see cref="WorkItem.TemplateVersion"/> from <c>v13</c> to
/// <c>v14</c>.
///
/// <see cref="WorkItemService"/> matches an action against the work item's own
/// frozen snapshot, not the live <see cref="ReAccreditationType"/> (the
/// snapshot is captured once, at submission). Without this migration, adding
/// the transition to the live type would only benefit work items submitted
/// after the deploy — and the items this story exists for are precisely the
/// ones already sitting in <c>updated</c> today, having been queried after
/// being duly made. They would keep offering only the "Continue review" route
/// back to <c>duly-made</c> that the story removes. This mirrors
/// <see cref="ReAccreditationSlaExtendQuerySnapshotMigration"/>'s v12→v13
/// precedent: a pure add of one transition, no retargeting of existing ones.
///
/// Like every migration in this folder it NEVER changes a work item's
/// <see cref="WorkItem.StateId"/>. An item sitting in <c>updated</c> stays in
/// <c>updated</c>, keeps its assignee and keeps its SLA clock; all it gains is
/// a forward route. Nothing needs a data fix or a re-seed.
///
/// Deliberately does NOT remove <c>continue-review-during-duly-made</c> from
/// the snapshot.
/// <see cref="ReAccreditationUpdatedOrigin.ResolveOriginatingStateId"/> derives
/// an item's origin by looking that transition's
/// <see cref="WorkItemTransition.ToStateId"/> up in this very snapshot, so
/// stripping it would make the origin unresolvable for exactly the items being
/// migrated — <see cref="ReAccreditationPaymentReceivedService"/> would then
/// refuse them and <see cref="WorkItemResponse.OriginStateId"/> would go null,
/// costing the frontend the discriminator it renders the new call to action
/// from. The old route stops being OFFERED (a frontend concern); it is not
/// removed.
///
/// The marker is the action id alone, which is sufficient here because
/// <c>payment-received-during-duly-made</c> is a brand-new id that no earlier
/// snapshot version can contain — unlike
/// <see cref="ReAccreditationSlaExtendQuerySnapshotMigration"/>, which had to
/// state-qualify its marker because <c>sla-extend</c> already existed. The
/// migration is idempotent: items whose snapshot already contains the
/// transition are skipped.
/// </summary>
internal sealed class ReAccreditationPaymentReceivedDulyMadeSnapshotMigration(
    ILogger<ReAccreditationPaymentReceivedDulyMadeSnapshotMigration> logger)
    : ReAccreditationSnapshotMigrationBase(logger)
{
    /// <summary>
    /// Action id of the transition this migration adds. Kept in sync with the
    /// literal declared in <see cref="ReAccreditationType"/>. Sufficient on its
    /// own as a presence marker — no pre-v14 snapshot can carry this id.
    /// </summary>
    private const string MarkerActionId = "payment-received-during-duly-made";

    // CallerInvocable: false is force-set here, matching the live
    // ReAccreditationType declaration. It is the security boundary of this
    // change, not a preference: this transition shares FromStateId 'updated'
    // with the four continue-review-during-* transitions, so the engine's
    // from-state guard cannot tell them apart. Were it invocable through the
    // generic action endpoint, a caller holding a 'submitted'-origin item in
    // 'updated' could fire it and skip duly making entirely — no payment date
    // captured and therefore no SLA clock ever started. The only way in is
    // ReAccreditationPaymentReceivedService, which resolves the origin from the
    // item's own audit history and refuses anything but 'duly-made'.
    private static readonly WorkItemTransition s_newTransition = new(
        MarkerActionId,
        "Payment received",
        "updated",
        "assessment-in-progress",
        CallerInvocable: false
    );

    public override string Name =>
        "ReAccreditation: add payment-received-during-duly-made transition to snapshot (v13 → v14)";

    protected override bool NeedsMigration(WorkItem workItem) =>
        workItem.TemplateSnapshot is not null
        && workItem.TemplateSnapshot.Transitions.All(t => t.ActionId != MarkerActionId);

    protected override void PatchSnapshot(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot!;
        workItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v14",
            States = snapshot.States,
            Transitions = snapshot.Transitions.Append(s_newTransition).ToList(),
        };
        workItem.TemplateVersion = "v14";
    }
}
