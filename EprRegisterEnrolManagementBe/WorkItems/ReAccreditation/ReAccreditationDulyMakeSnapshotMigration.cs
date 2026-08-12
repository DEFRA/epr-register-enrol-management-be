using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-316: reinstates the <c>duly-make</c> transition on the frozen
/// <see cref="WorkItemTemplateSnapshot"/> of every re-accreditation work item,
/// clears the two now-deleted <c>submitted</c>-state tasks from it, and bumps
/// <see cref="WorkItem.TemplateVersion"/> from <c>v10</c> to <c>v11</c>.
///
/// <see cref="WorkItemService"/> matches an action against the work item's own
/// frozen snapshot, not the live <see cref="ReAccreditationType"/>. Without this
/// migration every existing work item would be stranded twice over: it would
/// have no <c>duly-make</c> transition (so the new "Duly make" call to action
/// could not be honoured, and — with the auto-transition hook deleted — nothing
/// else could move it out of <c>submitted</c> either), and it would still
/// project two checklist tasks that no longer do anything. Adding the transition
/// to the live type alone only helps work items submitted after the deploy.
///
/// Note this migration REVERSES part of
/// <see cref="ReAccreditationDulyMadeSnapshotMigration"/>, which strips
/// <c>duly-make</c> as its v4 → v5 step. That is safe because that migration is
/// now gated on the item's template version being pre-v5, so it cannot see —
/// and therefore cannot re-strip — the transition this one re-adds. Without that
/// gate the two would fight on every boot, each undoing the other and leaving a
/// window in which no item could be duly made.
///
/// The migration is idempotent: items already at <c>v11</c> — snapshot carrying
/// <c>duly-make</c> and no <c>submitted</c> tasks — are skipped.
/// </summary>
internal sealed class ReAccreditationDulyMakeSnapshotMigration(
    ILogger<ReAccreditationDulyMakeSnapshotMigration> logger
) : IWorkItemMigration
{
    /// <summary>
    /// Kept in sync with the <c>duly-make</c> transition declared in
    /// <see cref="ReAccreditationType"/>. The two must agree: an item migrated
    /// with different flags would be judged by different rules than a freshly
    /// submitted one.
    /// </summary>
    private static readonly WorkItemTransition s_dulyMakeTransition = new(
        "duly-make",
        "Duly make",
        "submitted",
        "duly-made",
        RequiresAllTasksComplete: false,
        CallerInvocable: false
    );

    private const string TargetVersion = "v11";
    private const string SubmittedStateId = "submitted";

    public string Name =>
        "ReAccreditation: reinstate duly-make transition and clear submitted-state tasks "
        + "in snapshot (v10 → v11)";

    public async Task ApplyAsync(
        IWorkItemPersistence persistence,
        CancellationToken cancellationToken
    )
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
                    IncludeArchived: true
                ),
                cancellationToken
            );

            foreach (var candidate in result.Items)
            {
                if (!NeedsMigration(candidate))
                {
                    skipped++;
                    continue;
                }

                // QueryAsync omits AuditLog/Notes — fetch the full document
                // before saving so we do not wipe audit history on ReplaceAsync.
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
                    // Another instance migrated this item concurrently; it is
                    // already up to date.
                    logger.LogDebug(
                        "Concurrency conflict on work item {Id}; skipping — another instance "
                            + "already migrated it.",
                        full.Id
                    );
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
            Name,
            migrated,
            skipped
        );
    }

    /// <summary>
    /// Tests the two conditions independently rather than trusting the stored
    /// version string. An item that somehow has one half applied and not the
    /// other — a crash between two deploys, a hand-edited document — is still
    /// picked up and finished off.
    /// </summary>
    private static bool NeedsMigration(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot;
        if (snapshot is null)
        {
            // Nothing to patch. Such an item resolves its template from the live
            // registry (see WorkItemEngineRules.ResolveTemplate), so it already
            // sees v11 rules and is not stranded.
            return false;
        }

        var missingTransition = !snapshot.Transitions.Any(t =>
            string.Equals(t.ActionId, s_dulyMakeTransition.ActionId, StringComparison.OrdinalIgnoreCase)
        );
        var hasStaleSubmittedTasks = snapshot.GetTasksForState(SubmittedStateId).Count > 0;

        return missingTransition || hasStaleSubmittedTasks;
    }

    private static void PatchSnapshot(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot!;

        var transitions = snapshot
            .Transitions.Where(t =>
                !string.Equals(
                    t.ActionId,
                    s_dulyMakeTransition.ActionId,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .Append(s_dulyMakeTransition)
            .ToList();

        // Rebuild rather than mutate in place: TasksByState is a shared mutable
        // dictionary on the deserialised snapshot, and the original may be
        // case-sensitive depending on how it round-tripped through BSON. An
        // explicit OrdinalIgnoreCase copy matches WorkItemTemplateSnapshot.Capture.
        var tasksByState = new Dictionary<string, List<WorkItemTask>>(
            snapshot.TasksByState,
            StringComparer.OrdinalIgnoreCase
        )
        {
            // Empty list, not a removed key: 'submitted' is still a declared
            // state with a known (now empty) task list, and keeping the key
            // makes that explicit rather than leaving readers to infer it from
            // absence.
            [SubmittedStateId] = [],
        };

        workItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = TargetVersion,
            States = snapshot.States,
            Transitions = transitions,
            TasksByState = tasksByState,
        };
        workItem.TemplateVersion = TargetVersion;

        // Deliberately NOT touched:
        //
        // 1. Any recorded completion of the two deleted tasks in
        //    WorkItem.TaskStatusesByState / CompletedTaskIdsByState. Those are a
        //    record of work a regulator actually did. With the tasks gone from
        //    the snapshot the projection returns an empty list for 'submitted'
        //    and the stale bucket is inert, so deleting it would destroy history
        //    to no benefit.
        //
        // 2. The item's state. An item sitting in 'submitted' with both former
        //    tasks ticked is NOT auto-advanced to 'duly-made' here, even though
        //    the old hook would have advanced it. Duly making now requires a
        //    payment date that only the regulator can supply, and inventing one
        //    (today's date, the submission date) would anchor the 12-week SLA to
        //    a fiction. Such items simply present the "Duly make" call to action
        //    like any other — which is the correct destination for them.
    }
}
