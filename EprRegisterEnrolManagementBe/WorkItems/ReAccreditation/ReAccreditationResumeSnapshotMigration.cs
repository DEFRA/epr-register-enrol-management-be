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
    ILogger<ReAccreditationResumeSnapshotMigration> logger) : IWorkItemMigration
{
    /// <summary>
    /// Marker transition id used to test whether a snapshot already has the
    /// v7 transitions. Kept in sync with the four literal
    /// <c>resume-during-*</c> ids declared in <see cref="ReAccreditationType"/>.
    /// </summary>
    private const string MarkerActionId = "resume-during-duly-making";

    private static readonly IReadOnlyList<WorkItemTransition> s_newTransitions =
    [
        new WorkItemTransition("resume-during-duly-making", "Resume", "queried", "submitted", RequiresAllTasksComplete: false),
        new WorkItemTransition("resume-during-duly-made", "Resume", "queried", "duly-made", RequiresAllTasksComplete: false),
        new WorkItemTransition("resume-during-assessment", "Resume", "queried", "assessment-in-progress", RequiresAllTasksComplete: false),
        new WorkItemTransition("resume-during-decision", "Resume", "queried", "awaiting-decision", RequiresAllTasksComplete: false),
    ];

    public string Name => "ReAccreditation: add resume-during-* transitions to snapshot (v6 → v7)";

    public async Task ApplyAsync(IWorkItemPersistence persistence, CancellationToken cancellationToken)
    {
        var migrated = 0;
        var skipped = 0;
        var page = 1;
        const int pageSize = WorkItemQuery.MaxPageSize;

        while (true)
        {
            var result = await persistence.QueryAsync(
                new WorkItemQuery(
                    TypeIds: [ReAccreditationType.Id],
                    Page: page,
                    PageSize: pageSize,
                    IncludeArchived: true),
                cancellationToken);

            foreach (var candidate in result.Items)
            {
                if (!NeedsMigration(candidate))
                {
                    skipped++;
                    continue;
                }

                // QueryAsync omits AuditLog/Notes — fetch the full document before saving
                // so we do not accidentally wipe audit history on ReplaceAsync.
                var full = await persistence.GetByIdAsync(candidate.Id, cancellationToken);
                if (full is null || !NeedsMigration(full))
                {
                    skipped++;
                    continue;
                }

                PatchSnapshot(full);

                try
                {
                    await persistence.ReplaceAsync(full, cancellationToken);
                    migrated++;
                }
                catch (WorkItemConcurrencyException)
                {
                    // Another instance migrated this item concurrently; it is already up to date.
                    logger.LogDebug(
                        "Concurrency conflict on work item {Id}; skipping — another instance already migrated it.",
                        full.Id);
                    skipped++;
                }
            }

            var processed = (long)(page - 1) * pageSize + result.Items.Count;
            if (processed >= result.TotalCount)
            {
                break;
            }

            page++;
        }

        logger.LogInformation(
            "Migration '{Name}' complete: {Migrated} updated, {Skipped} already current.",
            Name, migrated, skipped);
    }

    private static bool NeedsMigration(WorkItem workItem) =>
        workItem.TemplateSnapshot is not null &&
        workItem.TemplateSnapshot.Transitions.All(t => t.ActionId != MarkerActionId);

    private static void PatchSnapshot(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot!;
        workItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v7",
            States = snapshot.States,
            Transitions = snapshot.Transitions.Concat(s_newTransitions).ToList(),
            TasksByState = snapshot.TasksByState
        };
        workItem.TemplateVersion = "v7";
    }
}
