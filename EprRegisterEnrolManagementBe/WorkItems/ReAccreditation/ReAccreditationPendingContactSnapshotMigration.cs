using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Adds the non-terminal <c>approved-pending-contact</c> state and the
/// <c>make-determination-approved</c> transition into it (from
/// <c>awaiting-decision</c>) to the frozen
/// <see cref="WorkItemTemplateSnapshot"/> of every re-accreditation work item,
/// and sets <see cref="WorkItem.TemplateVersion"/> to <c>v17</c> (RA-527,
/// two-step determination, step 1 for approvals).
///
/// <see cref="ReAccreditationApprovalService"/> checks the step 1 transition
/// against the work item's own frozen snapshot, not the live
/// <see cref="ReAccreditationType"/>, so without this migration an open case
/// submitted before this deploy could not be approved at all. No backfill:
/// existing Granted/Refused items keep their state; only the rules change.
///
/// Writes only <see cref="WorkItem.TemplateSnapshot"/> and
/// <see cref="WorkItem.TemplateVersion"/>. It never changes the state,
/// assignee, payload, SLA clock, notes or audit log. Idempotent: an item whose
/// snapshot already declares both the state and the transition is skipped,
/// and either one is only added when missing.
///
/// Registered last of the snapshot migrations (straight after the v16 one),
/// so it is the one that sets the final version.
/// </summary>
internal sealed class ReAccreditationPendingContactSnapshotMigration(
    ILogger<ReAccreditationPendingContactSnapshotMigration> logger)
    : ReAccreditationSnapshotMigrationBase(logger)
{
    private const string TargetVersion = "v17";

    // Frozen as literals, like the sibling migrations: a later change to the
    // live type must not silently change what this v17 migration writes.
    private static readonly WorkItemState s_approvedPendingContact = new(
        "approved-pending-contact",
        "Approved"
    );

    private static readonly WorkItemTransition s_makeDeterminationApproved = new(
        "make-determination-approved",
        "Make determination: Approved",
        "awaiting-decision",
        "approved-pending-contact",
        CallerInvocable: false
    );

    public override string Name =>
        "ReAccreditation: add approved-pending-contact state + make-determination-approved transition to snapshot (v16 → v17)";

    protected override bool NeedsMigration(WorkItem workItem) =>
        workItem.TemplateSnapshot is { } snapshot
        && (!DeclaresState(snapshot) || !DeclaresTransition(snapshot));

    protected override void PatchSnapshot(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot!;
        var states = snapshot.States.ToList();
        if (!DeclaresState(snapshot))
        {
            states.Add(s_approvedPendingContact);
        }

        var transitions = snapshot.Transitions.ToList();
        if (!DeclaresTransition(snapshot))
        {
            transitions.Add(s_makeDeterminationApproved);
        }

        workItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = TargetVersion,
            States = states,
            Transitions = transitions,
        };
        workItem.TemplateVersion = TargetVersion;
    }

    private static bool DeclaresState(WorkItemTemplateSnapshot snapshot) =>
        snapshot.States.Any(s =>
            string.Equals(s.Id, s_approvedPendingContact.Id, StringComparison.OrdinalIgnoreCase));

    private static bool DeclaresTransition(WorkItemTemplateSnapshot snapshot) =>
        snapshot.Transitions.Any(t =>
            string.Equals(
                t.ActionId,
                s_makeDeterminationApproved.ActionId,
                StringComparison.OrdinalIgnoreCase));
}
