using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Retargets the four <c>resume-during-*</c> transitions in the frozen
/// <see cref="WorkItemTemplateSnapshot"/> of every re-accreditation work item
/// to land on a new <c>updated</c> state instead of jumping straight back to
/// the originating state, adds that state plus the four
/// <c>continue-review-during-*</c> transitions out of it (RA-337), and bumps
/// <see cref="WorkItem.TemplateVersion"/> from <c>v7</c> to <c>v8</c>.
///
/// <see cref="WorkItemService"/> matches an action against the work item's
/// own frozen snapshot, not the live <see cref="ReAccreditationType"/> (the
/// snapshot is captured once, at submission). Without this migration, every
/// re-accreditation work item submitted before this deploy — including any
/// already sitting in <c>queried</c> today — would resume straight back to
/// its originating state on the old v7 path, never showing "Updated" in CM.
/// This mirrors <see cref="ReAccreditationResumeSnapshotMigration"/>'s
/// v6→v7 precedent, but retargets existing transitions instead of only
/// adding new ones, and — like that migration — never changes any work
/// item's current <c>StateId</c>: an item that already resumed under the old
/// v7 behaviour stays exactly where it is: this migration only changes what
/// a future resume can reach.
///
/// The migration is idempotent: items whose snapshot already contains the
/// <c>updated</c> state are skipped.
/// </summary>
internal sealed class ReAccreditationUpdatedStateSnapshotMigration(
    ILogger<ReAccreditationUpdatedStateSnapshotMigration> logger)
    : ReAccreditationSnapshotMigrationBase(logger)
{
    /// <summary>
    /// Marker state id used to test whether a snapshot already has the v8
    /// state/transitions. Kept in sync with <see cref="ReAccreditationType"/>.
    /// </summary>
    private const string MarkerStateId = "updated";

    private static readonly WorkItemState s_updatedState = new("updated", "Updated");

    private static readonly IReadOnlySet<string> s_resumeActionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "resume-during-duly-making",
        "resume-during-duly-made",
        "resume-during-assessment",
        "resume-during-decision",
    };

    // Security review (RA-311/MBE-1): CallerInvocable: false on all four —
    // kept in sync with the live ReAccreditationType.Transitions declaration.
    // These four share FromStateId 'updated', so if they were directly
    // invocable via the generic action endpoint a caller could pick any
    // target state regardless of which state the item was actually queried
    // from, bypassing ReAccreditationContinueReviewService's audit-history
    // resolution.
    private static readonly IReadOnlyList<WorkItemTransition> s_continueReviewTransitions =
    [
        new WorkItemTransition("continue-review-during-duly-making", "Continue review", "updated", "submitted", CallerInvocable: false),
        new WorkItemTransition("continue-review-during-duly-made", "Continue review", "updated", "duly-made", CallerInvocable: false),
        new WorkItemTransition("continue-review-during-assessment", "Continue review", "updated", "assessment-in-progress", CallerInvocable: false),
        new WorkItemTransition("continue-review-during-decision", "Continue review", "updated", "awaiting-decision", CallerInvocable: false),
    ];

    public override string Name => "ReAccreditation: add 'updated' state + continue-review-during-* transitions to snapshot (v7 → v8)";

    protected override bool NeedsMigration(WorkItem workItem) =>
        workItem.TemplateSnapshot is not null &&
        workItem.TemplateSnapshot.States.All(s => s.Id != MarkerStateId);

    protected override void PatchSnapshot(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot!;

        // CallerInvocable is force-set to false here (not just preserved via
        // `with`) so a snapshot migrated by an older build of this migration
        // — from before the RA-311/MBE-1 security review added the flag —
        // is brought up to the safe value too, not just items migrated fresh.
        var retargetedTransitions = snapshot.Transitions.Select(t =>
            s_resumeActionIds.Contains(t.ActionId)
                ? t with { ToStateId = s_updatedState.Id, CallerInvocable = false }
                : t);

        workItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v8",
            States = snapshot.States.Append(s_updatedState).ToList(),
            Transitions = retargetedTransitions.Concat(s_continueReviewTransitions).ToList()
        };
        workItem.TemplateVersion = "v8";
    }
}
