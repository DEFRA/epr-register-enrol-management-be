using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Adds the <c>start-assessment</c> transition ("Assessment started",
/// <c>duly-made → assessment-in-progress</c>, RA-595) to the frozen
/// <see cref="WorkItemTemplateSnapshot"/> of every re-accreditation work item
/// and sets <see cref="WorkItem.TemplateVersion"/> to <c>v15</c>.
///
/// <see cref="WorkItemService"/> matches an action against the work item's own
/// frozen snapshot, not the live <see cref="ReAccreditationType"/>, so without
/// this migration only items submitted after the deploy could be started with
/// the new step. This is the "add" step of RA-595's add, switch, remove
/// rollout: <c>payment-received</c> is deliberately left in place so the
/// currently deployed management-fe keeps working; a later (v16) migration
/// removes it.
///
/// Writes only <see cref="WorkItem.TemplateSnapshot"/> and
/// <see cref="WorkItem.TemplateVersion"/>. It never changes the state,
/// assignee, payload (including the payment date), SLA clock, notes or audit
/// log. Idempotent: an item whose snapshot already has <c>start-assessment</c>
/// is skipped, so a repeat boot writes nothing.
///
/// Registered after every earlier snapshot migration, so an older item is
/// brought up to v14 first and lands on v15 here.
/// </summary>
internal sealed class ReAccreditationStartAssessmentSnapshotMigration(
    ILogger<ReAccreditationStartAssessmentSnapshotMigration> logger)
    : ReAccreditationSnapshotMigrationBase(logger)
{
    private const string TargetVersion = "v15";

    // Frozen as a literal, like the sibling migrations: a later change to the
    // live type must not silently change what this v15 migration writes.
    private static readonly WorkItemTransition s_newTransition = new(
        "start-assessment",
        "Assessment started",
        "duly-made",
        "assessment-in-progress"
    );

    public override string Name =>
        "ReAccreditation: add start-assessment transition to snapshot (v14 → v15)";

    protected override bool NeedsMigration(WorkItem workItem) =>
        workItem.TemplateSnapshot is not null
        && !workItem.TemplateSnapshot.Transitions.Any(t =>
            string.Equals(t.ActionId, s_newTransition.ActionId, StringComparison.OrdinalIgnoreCase));

    protected override void PatchSnapshot(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot!;
        workItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = TargetVersion,
            States = snapshot.States,
            Transitions = snapshot.Transitions.Append(s_newTransition).ToList(),
        };
        workItem.TemplateVersion = TargetVersion;
    }
}
