using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Removes the <c>payment-received</c> transition from the frozen
/// <see cref="WorkItemTemplateSnapshot"/> of every re-accreditation work item
/// and sets <see cref="WorkItem.TemplateVersion"/> to <c>v16</c> (RA-595,
/// "remove" step of the add, switch, remove rollout).
///
/// <see cref="WorkItemService"/> matches an action against the work item's own
/// frozen snapshot, not the live <see cref="ReAccreditationType"/>, so without
/// this migration a stale page or hand-crafted request could still apply
/// <c>payment-received</c> to an existing case. Once migrated, it is rejected
/// as an unknown action, exactly as on a case created at v16.
///
/// The v16 rules also carry <c>start-assessment</c>. It is normally added by
/// <see cref="ReAccreditationStartAssessmentSnapshotMigration"/>, which runs
/// first; this migration appends it too if it is somehow missing, so a v16
/// label always means the v16 rules.
///
/// Writes only <see cref="WorkItem.TemplateSnapshot"/> and
/// <see cref="WorkItem.TemplateVersion"/>. It never changes the state,
/// assignee, payload (including the payment date), SLA clock, notes or audit
/// log, so existing "Payment received" history entries stay as they are.
/// Idempotent: an item whose snapshot has <c>start-assessment</c> and no
/// <c>payment-received</c> is skipped, so a repeat boot writes nothing.
///
/// Registered straight after the v15 one. Later snapshot migrations follow
/// it; the last one registered sets the final version.
/// </summary>
internal sealed class ReAccreditationRemovePaymentReceivedSnapshotMigration(
    ILogger<ReAccreditationRemovePaymentReceivedSnapshotMigration> logger)
    : ReAccreditationSnapshotMigrationBase(logger)
{
    private const string TargetVersion = "v16";
    private const string RemovedActionId = "payment-received";

    // Frozen as a literal, like the sibling migrations: a later change to the
    // live type must not silently change what this v16 migration writes.
    private static readonly WorkItemTransition s_startAssessment = new(
        "start-assessment",
        "Assessment started",
        "duly-made",
        "assessment-in-progress"
    );

    public override string Name =>
        "ReAccreditation: remove payment-received transition from snapshot (v15 → v16)";

    protected override bool NeedsMigration(WorkItem workItem) =>
        workItem.TemplateSnapshot is { } snapshot
        && (Declares(snapshot, RemovedActionId) || !Declares(snapshot, s_startAssessment.ActionId));

    protected override void PatchSnapshot(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot!;
        var transitions = snapshot
            .Transitions.Where(t => !IsAction(t, RemovedActionId))
            .ToList();
        if (!Declares(snapshot, s_startAssessment.ActionId))
        {
            transitions.Add(s_startAssessment);
        }

        workItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = TargetVersion,
            States = snapshot.States,
            Transitions = transitions,
        };
        workItem.TemplateVersion = TargetVersion;
    }

    private static bool Declares(WorkItemTemplateSnapshot snapshot, string actionId) =>
        snapshot.Transitions.Any(t => IsAction(t, actionId));

    private static bool IsAction(WorkItemTransition transition, string actionId) =>
        string.Equals(transition.ActionId, actionId, StringComparison.OrdinalIgnoreCase);
}
