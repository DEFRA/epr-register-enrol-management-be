using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Retargets the <c>resume-during-duly-made</c> transition in the frozen
/// <see cref="WorkItemTemplateSnapshot"/> of every re-accreditation work item
/// from the <c>updated</c> waypoint to <c>assessment-in-progress</c> (RA-523),
/// and bumps <see cref="WorkItem.TemplateVersion"/> from <c>v13</c> to
/// <c>v14</c>.
///
/// <see cref="WorkItemService"/> matches an action against the work item's own
/// frozen snapshot, not the live <see cref="ReAccreditationType"/> (the
/// snapshot is captured once, at submission). Without this migration, an
/// application queried after it was duly made — including any already sitting
/// in <c>queried</c> today — would resume onto <c>updated</c> under its old
/// snapshot and could only move on via <c>continue-review-during-duly-made</c>,
/// returning to <c>duly-made</c>: the "came back as Duly made a second time"
/// behaviour this story removes. Mirrors
/// <see cref="ReAccreditationUpdatedStateSnapshotMigration"/>'s v7→v8
/// precedent, which retargeted the same family of transitions, but touches
/// only the single <c>resume-during-duly-made</c> edge.
///
/// Only <c>resume-during-duly-made</c> is retargeted. The other three
/// <c>resume-during-*</c> keep landing on <c>updated</c>: a submitted-origin
/// item must still be duly made, and assessment/decision-origin items must
/// still be reviewed via <c>continue-review</c>.
///
/// Like every migration in this folder it NEVER changes a work item's
/// <see cref="WorkItem.StateId"/>. An item that already resumed onto
/// <c>updated</c> under the old snapshot stays there — this only changes where
/// a <em>future</em> resume can land. Those already-resumed items are a
/// bounded remnant (dev only) handled by a re-seed, not by moving state here.
///
/// <c>continue-review-during-duly-made</c> is deliberately left in the
/// snapshot: it is dead for items resumed under the new rule (they never enter
/// <c>updated</c>), but it is the only way out for any item already parked
/// there, and it is the declaration
/// <see cref="ReAccreditationUpdatedOrigin.ResolveOriginatingStateId"/> reads
/// to resolve those items' origin.
///
/// The migration is idempotent: items whose <c>resume-during-duly-made</c>
/// already targets <c>assessment-in-progress</c> are skipped.
/// </summary>
internal sealed class ReAccreditationResumeDulyMadeAssessmentSnapshotMigration(
    ILogger<ReAccreditationResumeDulyMadeAssessmentSnapshotMigration> logger)
    : ReAccreditationSnapshotMigrationBase(logger)
{
    private const string ActionId = "resume-during-duly-made";
    private const string NewToStateId = "assessment-in-progress";

    public override string Name =>
        "ReAccreditation: retarget resume-during-duly-made to assessment-in-progress in snapshot (v13 → v14)";

    protected override bool NeedsMigration(WorkItem workItem) =>
        workItem.TemplateSnapshot is not null
        && workItem.TemplateSnapshot.Transitions.Any(t =>
            string.Equals(t.ActionId, ActionId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(t.ToStateId, NewToStateId, StringComparison.OrdinalIgnoreCase)
        );

    protected override void PatchSnapshot(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot!;

        var retargeted = snapshot.Transitions.Select(t =>
            string.Equals(t.ActionId, ActionId, StringComparison.OrdinalIgnoreCase)
                ? t with { ToStateId = NewToStateId }
                : t);

        workItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v14",
            States = snapshot.States,
            Transitions = retargeted.ToList(),
        };
        workItem.TemplateVersion = "v14";
    }
}
