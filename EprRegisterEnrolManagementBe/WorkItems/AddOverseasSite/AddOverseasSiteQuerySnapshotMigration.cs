using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;

/// <summary>
/// RA-698: brings the frozen <see cref="WorkItemTemplateSnapshot"/> of every
/// add-ORS work item onto the v3 rules and sets
/// <see cref="WorkItem.TemplateVersion"/> to <c>v3</c>. Querying an add-ORS
/// item and answering the query go through bespoke services that apply the
/// <c>query-during-*</c>, <c>resume-during-*</c> and
/// <c>continue-review-during-*</c> transitions, so each of those declarations
/// must be in the snapshot and <c>CallerInvocable: false</c>: the generic
/// action endpoint would otherwise let a caller pick a target the audit
/// history does not support, and skip the query stamp and audit entry. Any of
/// them a snapshot lacks is added, and every <c>withdraw*</c> declaration is
/// made non-invocable as v2 did, so a v1 item reaches the v3 rules in one pass
/// whichever order the module's migrations run in.
///
/// Writes only <see cref="WorkItem.TemplateSnapshot"/> and
/// <see cref="WorkItem.TemplateVersion"/>; it never changes the state,
/// assignee, payload, SLA clock, notes or audit log. Idempotent: an item that
/// is on v3 with all of those declarations non-invocable (or has no snapshot,
/// which the engine resolves to the live v3 type) is skipped. Built on
/// <see cref="WorkItemSnapshotMigrationBase"/> like
/// <see cref="AddOverseasSiteWithdrawNotCallerInvocableSnapshotMigration"/>,
/// so it re-reads the full document before saving and treats a concurrency
/// conflict as another instance having migrated the item.
/// </summary>
internal sealed class AddOverseasSiteQuerySnapshotMigration(ILogger<AddOverseasSiteQuerySnapshotMigration> logger)
    : WorkItemSnapshotMigrationBase(AddOverseasSiteType.Id, logger)
{
    private const string TargetVersion = "v3";
    private const string QueryName = "Query";
    private const string ResumeName = "Resume";
    private const string ContinueReviewName = "Continue review";
    private const string Queried = "queried";
    private const string Updated = "updated";

    // Frozen as literals, like the other snapshot migrations: a later change
    // to the live type must not silently change what this v3 migration writes.
    private static readonly IReadOnlyList<WorkItemTransition> s_queryTransitions =
    [
        new("query-during-duly-making", QueryName, "submitted", Queried, false),
        new("query-during-duly-made", QueryName, "duly-made", Queried, false),
        new("query-during-assessment", QueryName, "assessment-in-progress", Queried, false),
        new("query-during-assessment", QueryName, "assessment-started", Queried, false),
        new("query-during-decision", QueryName, "awaiting-decision", Queried, false),
        new("resume-during-duly-making", ResumeName, Queried, Updated, false),
        new("resume-during-duly-made", ResumeName, Queried, "assessment-in-progress", false),
        new("resume-during-assessment", ResumeName, Queried, Updated, false),
        new("resume-during-decision", ResumeName, Queried, Updated, false),
        new("continue-review-during-duly-making", ContinueReviewName, Updated, "submitted", false),
        new("continue-review-during-duly-made", ContinueReviewName, Updated, "duly-made", false),
        new("continue-review-during-assessment", ContinueReviewName, Updated, "assessment-in-progress", false),
        new("continue-review-during-decision", ContinueReviewName, Updated, "awaiting-decision", false),
    ];

    public override string Name =>
        "AddOverseasSite: query, resume and continue-review transitions bespoke-only in snapshot (v2 → v3)";

    protected override bool NeedsMigration(WorkItem workItem) =>
        workItem.TemplateSnapshot is { } snapshot
        && (
            !string.Equals(snapshot.TemplateVersion, TargetVersion, StringComparison.Ordinal)
            || snapshot.Transitions.Any(IsCallerInvocableBespokeOrWithdraw)
            || s_queryTransitions.Any(t => !HasTransition(snapshot, t))
        );

    protected override void PatchSnapshot(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot!;

        var transitions = snapshot
            .Transitions.Select(t => IsCallerInvocableBespokeOrWithdraw(t) ? t with { CallerInvocable = false } : t)
            .ToList();
        transitions.AddRange(s_queryTransitions.Where(t => !HasTransition(snapshot, t)));

        workItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = TargetVersion,
            States = snapshot.States,
            Transitions = transitions,
        };
        workItem.TemplateVersion = TargetVersion;
    }

    private static bool HasTransition(WorkItemTemplateSnapshot snapshot, WorkItemTransition expected) =>
        snapshot.Transitions.Any(t =>
            string.Equals(t.ActionId, expected.ActionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.FromStateId, expected.FromStateId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.ToStateId, expected.ToStateId, StringComparison.OrdinalIgnoreCase)
        );

    private static bool IsCallerInvocableBespokeOrWithdraw(WorkItemTransition transition) =>
        transition.CallerInvocable
        && (
            transition.ActionId.StartsWith("query-during-", StringComparison.OrdinalIgnoreCase)
            || transition.ActionId.StartsWith("resume-during-", StringComparison.OrdinalIgnoreCase)
            || transition.ActionId.StartsWith("continue-review-during-", StringComparison.OrdinalIgnoreCase)
            || transition.ActionId.StartsWith("withdraw", StringComparison.OrdinalIgnoreCase)
        );
}
