using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Brings the frozen <see cref="WorkItemTemplateSnapshot"/> of every
/// re-accreditation work item onto the v18 rules (RA-527 follow-up, starting a
/// case keeps it "Duly made") and sets <see cref="WorkItem.TemplateVersion"/>
/// to <c>v18</c>:
/// <list type="bullet">
///   <item>adds the non-terminal <c>assessment-started</c> state ("Duly
///   made"), declared straight after <c>duly-made</c>;</item>
///   <item>retargets <c>start-assessment</c> to
///   <c>duly-made → assessment-started</c> (same action id, display name and
///   caller-invocability);</item>
///   <item>adds the four exits from <c>assessment-started</c>, each reusing an
///   existing action id: <c>submit-for-decision</c> (→
///   <c>awaiting-decision</c>, not caller-invocable),
///   <c>query-during-assessment</c> (→ <c>queried</c>),
///   <c>withdraw-during-assessment</c> (→ <c>withdrawn</c>) and
///   <c>sla-extend</c> (→ <c>assessment-in-progress</c>, not
///   caller-invocable).</item>
/// </list>
///
/// The services check transitions against the work item's own frozen
/// snapshot, not the live <see cref="ReAccreditationType"/>, so without this
/// migration an open v17 case would still start into
/// <c>assessment-in-progress</c> ("Updated"), and would have no exits from
/// the new state.
///
/// Writes only <see cref="WorkItem.TemplateSnapshot"/> and
/// <see cref="WorkItem.TemplateVersion"/>. It never changes the state,
/// assignee, payload, SLA clock, notes or audit log, so a case already in
/// <c>assessment-in-progress</c> stays there and stays "Updated" (agreed: no
/// data migration). Idempotent: an item whose snapshot already declares the
/// frozen state and all five transitions is skipped. Each change is only made
/// when needed, so an item snapshotted by an earlier, unreleased build of v18
/// (which carried only some of the exits) gains just what it lacks. A
/// declaration keyed the same (state id, or action id plus from-state) but
/// differing from the frozen one is replaced in place.
///
/// Registered last of the snapshot migrations (straight after the v17 one),
/// so it is the one that sets the final version.
/// </summary>
internal sealed class ReAccreditationAssessmentStartedSnapshotMigration(
    ILogger<ReAccreditationAssessmentStartedSnapshotMigration> logger)
    : ReAccreditationSnapshotMigrationBase(logger)
{
    private const string TargetVersion = "v18";
    private const string DeclaredAfterStateId = "duly-made";
    private const string StartedStateId = "assessment-started";

    // Frozen as literals, like the sibling migrations: a later change to the
    // live type must not silently change what this v18 migration writes.
    private static readonly WorkItemState s_state = new(StartedStateId, "Duly made");

    private static readonly WorkItemTransition[] s_transitions =
    [
        new("start-assessment", "Assessment started", "duly-made", StartedStateId),
        new(
            "submit-for-decision",
            "Submit for decision",
            StartedStateId,
            "awaiting-decision",
            CallerInvocable: false
        ),
        new("query-during-assessment", "Query", StartedStateId, "queried"),
        new("withdraw-during-assessment", "Withdraw", StartedStateId, "withdrawn"),
        new(
            "sla-extend",
            "Extend SLA",
            StartedStateId,
            "assessment-in-progress",
            CallerInvocable: false
        ),
    ];

    public override string Name =>
        "ReAccreditation: add assessment-started state, retarget start-assessment to it and add "
        + "its submit-for-decision, query, withdraw and sla-extend exits, in snapshot (v17 → v18)";

    protected override bool NeedsMigration(WorkItem workItem) =>
        workItem.TemplateSnapshot is { } snapshot
        && (
            !snapshot.States.Contains(s_state)
            || s_transitions.Any(t => !snapshot.Transitions.Contains(t))
        );

    protected override void PatchSnapshot(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot!;

        var states = snapshot.States.Select(s => IsSameState(s, s_state) ? s_state : s).ToList();
        if (!states.Any(s => IsSameState(s, s_state)))
        {
            // Declared straight after duly-made, as the live type declares it.
            var after = states.FindIndex(s => IsState(s, DeclaredAfterStateId));
            states.Insert(after < 0 ? states.Count : after + 1, s_state);
        }

        var transitions = snapshot
            .Transitions.Select(t => s_transitions.FirstOrDefault(f => IsSameTransition(f, t)) ?? t)
            .ToList();
        transitions.AddRange(
            s_transitions.Where(f => !snapshot.Transitions.Any(t => IsSameTransition(f, t))));

        workItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = TargetVersion,
            States = states,
            Transitions = transitions,
        };
        workItem.TemplateVersion = TargetVersion;
    }

    private static bool IsSameState(WorkItemState a, WorkItemState b) => IsState(a, b.Id);

    private static bool IsState(WorkItemState state, string id) =>
        string.Equals(state.Id, id, StringComparison.OrdinalIgnoreCase);

    // The engine keys transitions by action id per from-state (sla-extend,
    // submit-for-decision and friends each appear more than once), so that
    // pair is the identity of a declaration.
    private static bool IsSameTransition(WorkItemTransition a, WorkItemTransition b) =>
        string.Equals(a.ActionId, b.ActionId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.FromStateId, b.FromStateId, StringComparison.OrdinalIgnoreCase);
}
