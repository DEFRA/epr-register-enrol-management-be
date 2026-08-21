using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Adds the four <c>resume-during-*</c> transitions (RA-311/MBE-1) to the
/// frozen <see cref="WorkItemTemplateSnapshot"/> of every re-accreditation
/// work item and bumps <see cref="WorkItem.TemplateVersion"/> from
/// <c>v6</c> to <c>v7</c>.
///
/// <see cref="WorkItemService"/> matches an action against the work item's
/// own frozen snapshot, not the live <see cref="ReAccreditationType"/>
/// (the snapshot is captured once, at submission). Without this migration,
/// every re-accreditation work item submitted before this deploy —
/// including any already sitting in <c>queried</c> today — has no way out
/// of <c>queried</c>: adding the transitions to the live type only benefits
/// work items submitted after the deploy. This mirrors
/// <see cref="ReAccreditationDulyMadeSnapshotMigration"/>'s v4→v5 precedent,
/// but adds transitions instead of removing one, and never auto-transitions
/// any work item's state — it only extends what a future action can reach.
///
/// The migration is idempotent: items whose snapshot already contains
/// <c>resume-during-duly-making</c> are skipped.
/// </summary>
internal sealed class ReAccreditationResumeSnapshotMigration(
    ILogger<ReAccreditationResumeSnapshotMigration> logger)
    : ReAccreditationSnapshotMigrationBase(logger)
{
    /// <summary>
    /// Marker transition id used to test whether a snapshot already has the
    /// v7 transitions. Kept in sync with the four literal
    /// <c>resume-during-*</c> ids declared in <see cref="ReAccreditationType"/>.
    /// </summary>
    private const string MarkerActionId = "resume-during-duly-making";

    // Security review (RA-311/MBE-1): CallerInvocable: false on all four —
    // kept in sync with the live ReAccreditationType.Transitions declaration.
    // These four share FromStateId 'queried', so if they were directly
    // invocable via the generic action endpoint a caller could pick any
    // target state regardless of which state the item was actually queried
    // from, bypassing ReAccreditationResumeService's audit-history
    // resolution.
    private static readonly IReadOnlyList<WorkItemTransition> s_newTransitions =
    [
        new WorkItemTransition("resume-during-duly-making", "Resume", "queried", "submitted", CallerInvocable: false),
        new WorkItemTransition("resume-during-duly-made", "Resume", "queried", "duly-made", CallerInvocable: false),
        new WorkItemTransition("resume-during-assessment", "Resume", "queried", "assessment-in-progress", CallerInvocable: false),
        new WorkItemTransition("resume-during-decision", "Resume", "queried", "awaiting-decision", CallerInvocable: false),
    ];

    public override string Name => "ReAccreditation: add resume-during-* transitions to snapshot (v6 → v7)";

    protected override bool NeedsMigration(WorkItem workItem) =>
        workItem.TemplateSnapshot is not null &&
        workItem.TemplateSnapshot.Transitions.All(t => t.ActionId != MarkerActionId);

    protected override void PatchSnapshot(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot!;
        workItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v7",
            States = snapshot.States,
            Transitions = snapshot.Transitions.Concat(s_newTransitions).ToList()
        };
        workItem.TemplateVersion = "v7";
    }
}
