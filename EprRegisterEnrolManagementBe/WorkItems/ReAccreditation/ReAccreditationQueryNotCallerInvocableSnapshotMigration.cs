using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Brings the frozen <see cref="WorkItemTemplateSnapshot"/> of every
/// re-accreditation work item onto the v19 rules (RA-000) and sets
/// <see cref="WorkItem.TemplateVersion"/> to <c>v19</c>: every
/// <c>query-during-*</c> declaration (<c>-duly-making</c>, <c>-duly-made</c>,
/// <c>-assessment</c> from both <c>assessment-in-progress</c> and
/// <c>assessment-started</c>, <c>-decision</c>) becomes
/// <c>CallerInvocable: false</c>.
///
/// The generic action endpoint checks <c>CallerInvocable</c> against the
/// work item's own frozen snapshot, so without this migration an open v18
/// case could still be queried through
/// <c>POST /work-items/{id}/actions/query-during-*</c>, bypassing
/// <see cref="ReAccreditationQueryService"/> (no self-assign, no
/// <c>currentQuery</c> stamp, no <c>application-queried</c> audit entry).
///
/// Only the flag changes: action ids, display names, from- and to-states are
/// kept as each item's snapshot has them. Writes only
/// <see cref="WorkItem.TemplateSnapshot"/> and
/// <see cref="WorkItem.TemplateVersion"/>; it never changes the state,
/// assignee, payload, SLA clock, notes or audit log. Idempotent: an item with
/// no caller-invocable <c>query-during-*</c> declaration is skipped.
///
/// Registered after <see cref="ReAccreditationAssessmentStartedSnapshotMigration"/>,
/// which adds <c>query-during-assessment</c> from <c>assessment-started</c>
/// as caller-invocable, so this is the one that sets the final version.
/// </summary>
internal sealed class ReAccreditationQueryNotCallerInvocableSnapshotMigration(
    ILogger<ReAccreditationQueryNotCallerInvocableSnapshotMigration> logger)
    : ReAccreditationSnapshotMigrationBase(logger)
{
    private const string TargetVersion = "v19";

    // Frozen as literals, like the sibling migrations: a later change to the
    // live type must not silently change what this v19 migration writes.
    private static readonly HashSet<string> s_queryActionIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "query-during-duly-making",
        "query-during-duly-made",
        "query-during-assessment",
        "query-during-decision",
    };

    public override string Name =>
        "ReAccreditation: make the query-during-* transitions not caller-invocable in snapshot (v18 → v19)";

    protected override bool NeedsMigration(WorkItem workItem) =>
        workItem.TemplateSnapshot is { } snapshot
        && snapshot.Transitions.Any(IsCallerInvocableQuery);

    protected override void PatchSnapshot(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot!;

        workItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = TargetVersion,
            States = snapshot.States,
            Transitions = snapshot
                .Transitions.Select(t => IsCallerInvocableQuery(t) ? t with { CallerInvocable = false } : t)
                .ToList(),
        };
        workItem.TemplateVersion = TargetVersion;
    }

    private static bool IsCallerInvocableQuery(WorkItemTransition transition) =>
        transition.CallerInvocable && s_queryActionIds.Contains(transition.ActionId);
}
