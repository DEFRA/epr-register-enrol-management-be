using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Brings the frozen <see cref="WorkItemTemplateSnapshot"/> of every
/// re-accreditation work item onto the v17 rules (RA-527, two-step
/// determination) and sets <see cref="WorkItem.TemplateVersion"/> to
/// <c>v17</c>:
/// <list type="bullet">
///   <item>adds the non-terminal <c>approved-pending-contact</c> state, the
///   <c>make-determination-approved</c> transition into it (from
///   <c>awaiting-decision</c>, step 1) and the <c>confirm-contact-approved</c>
///   transition out of it (to <c>approved</c>, step 2);</item>
///   <item>adds the non-terminal <c>rejected-pending-contact</c> state, the
///   <c>make-determination-rejected</c> transition into it and the
///   <c>confirm-contact-rejected</c> transition out of it (to
///   <c>rejected</c>);</item>
///   <item>removes the single-step <c>reject</c> transition
///   (<c>awaiting-decision</c> → <c>rejected</c>), so a refusal takes both
///   steps.</item>
/// </list>
///
/// The approval, refusal and confirm-contact services check these
/// transitions against the work item's own frozen snapshot, not the live
/// <see cref="ReAccreditationType"/>, so without this migration an open case
/// submitted before this deploy could not be determined at all. No backfill:
/// existing Granted/Refused items keep their state; only the rules change.
///
/// Writes only <see cref="WorkItem.TemplateSnapshot"/> and
/// <see cref="WorkItem.TemplateVersion"/>. It never changes the state,
/// assignee, payload, SLA clock, notes or audit log, so existing "Reject"
/// history entries stay as they are. Idempotent: an item whose snapshot
/// already declares both states and all four transitions, and no
/// <c>reject</c>, is skipped, and each change is only made when needed — so an
/// item stamped v17 by an earlier build of this migration (RA-527 tasks 003
/// and 004, approvals only) gains just what it lacks. An item stamped v17
/// while the step 1 refusal state had its earlier, provisional label has just
/// that state's declaration replaced with the frozen one ("Refused", RA-527
/// task 010); v17 was unreleased, so this stays v17 rather than becoming a
/// v18.
///
/// Registered straight after the v16 one. The v18
/// <see cref="ReAccreditationAssessmentStartedSnapshotMigration"/> (RA-527
/// follow-up) follows it and sets the final version.
/// </summary>
internal sealed class ReAccreditationPendingContactSnapshotMigration(
    ILogger<ReAccreditationPendingContactSnapshotMigration> logger)
    : ReAccreditationSnapshotMigrationBase(logger)
{
    private const string TargetVersion = "v17";
    private const string RemovedActionId = "reject";

    // Frozen as literals, like the sibling migrations: a later change to the
    // live type must not silently change what this v17 migration writes.
    private static readonly WorkItemState[] s_states =
    [
        new("approved-pending-contact", "Approved"),
        new("rejected-pending-contact", "Refused"),
    ];

    private static readonly WorkItemTransition[] s_transitions =
    [
        new(
            "make-determination-approved",
            "Make determination: Approved",
            "awaiting-decision",
            "approved-pending-contact",
            CallerInvocable: false
        ),
        new(
            "confirm-contact-approved",
            "Confirm applicant contact",
            "approved-pending-contact",
            "approved",
            CallerInvocable: false
        ),
        new(
            "make-determination-rejected",
            "Make determination: Refused",
            "awaiting-decision",
            "rejected-pending-contact",
            CallerInvocable: false
        ),
        new(
            "confirm-contact-rejected",
            "Confirm applicant contact",
            "rejected-pending-contact",
            "rejected",
            CallerInvocable: false
        ),
    ];

    public override string Name =>
        "ReAccreditation: add approved-/rejected-pending-contact states + make-determination-* and "
        + "confirm-contact-* transitions, remove reject, correct an earlier pending-state label, "
        + "in snapshot (v16 → v17)";

    protected override bool NeedsMigration(WorkItem workItem) =>
        workItem.TemplateSnapshot is { } snapshot
        && (
            s_states.Any(s => !DeclaresState(snapshot, s))
            || snapshot.States.Any(DiffersFromFrozenPendingState)
            || s_transitions.Any(t => !Declares(snapshot, t.ActionId))
            || Declares(snapshot, RemovedActionId)
        );

    protected override void PatchSnapshot(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot!;
        // A pending-contact state declared by an earlier build of v17 with a
        // different label is replaced in place by the frozen one.
        var states = snapshot.States.Select(s => DiffersFromFrozenPendingState(s) ? FrozenState(s) : s).ToList();
        states.AddRange(s_states.Where(s => !DeclaresState(snapshot, s)));

        var transitions = snapshot
            .Transitions.Where(t => !IsAction(t, RemovedActionId))
            .ToList();
        transitions.AddRange(s_transitions.Where(t => !Declares(snapshot, t.ActionId)));

        workItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = TargetVersion,
            States = states,
            Transitions = transitions,
        };
        workItem.TemplateVersion = TargetVersion;
    }

    private static bool DeclaresState(WorkItemTemplateSnapshot snapshot, WorkItemState state) =>
        snapshot.States.Any(s => IsSameState(s, state));

    // One of the pending-contact states, declared differently from the frozen
    // v17 declaration (for example under an earlier label).
    private static bool DiffersFromFrozenPendingState(WorkItemState state) =>
        s_states.Any(s => IsSameState(s, state) && s != state);

    private static WorkItemState FrozenState(WorkItemState state) =>
        s_states.Single(s => IsSameState(s, state));

    private static bool IsSameState(WorkItemState a, WorkItemState b) =>
        string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);

    private static bool Declares(WorkItemTemplateSnapshot snapshot, string actionId) =>
        snapshot.Transitions.Any(t => IsAction(t, actionId));

    private static bool IsAction(WorkItemTransition transition, string actionId) =>
        string.Equals(transition.ActionId, actionId, StringComparison.OrdinalIgnoreCase);
}
