using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Adds the <c>withdraw-during-query</c> transition (RA-252) to the frozen
/// <see cref="WorkItemTemplateSnapshot"/> of every re-accreditation work
/// item and bumps <see cref="WorkItem.TemplateVersion"/> from <c>v8</c> to
/// <c>v9</c>.
///
/// <see cref="WorkItemService"/> matches an action against the work item's
/// own frozen snapshot, not the live <see cref="ReAccreditationType"/> (the
/// snapshot is captured once, at submission). Without this migration, every
/// re-accreditation work item submitted before this deploy — including any
/// already sitting in <c>queried</c> today — has no way to reach
/// <c>withdrawn</c> from <c>queried</c>: adding the transition to the live
/// type only benefits work items submitted after the deploy. This mirrors
/// <see cref="ReAccreditationResumeSnapshotMigration"/>'s v6→v7 precedent.
///
/// The migration is idempotent: items whose snapshot already contains
/// <c>withdraw-during-query</c> are skipped.
/// </summary>
internal sealed class ReAccreditationWithdrawQuerySnapshotMigration(
    ILogger<ReAccreditationWithdrawQuerySnapshotMigration> logger
) : IWorkItemMigration
{
    /// <summary>
    /// Marker transition id used to test whether a snapshot already has the
    /// v9 transition. Kept in sync with the literal <c>withdraw-during-query</c>
    /// id declared in <see cref="ReAccreditationType"/>.
    /// </summary>
    private const string MarkerActionId = "withdraw-during-query";

    private static readonly WorkItemTransition s_newTransition = new(
        "withdraw-during-query",
        "Withdraw",
        "queried",
        "withdrawn"
    );

    public string Name =>
        "ReAccreditation: add withdraw-during-query transition to snapshot (v8 → v9)";

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

    private static bool NeedsMigration(WorkItem workItem) =>
        workItem.TemplateSnapshot is not null
        && workItem.TemplateSnapshot.Transitions.All(t => t.ActionId != MarkerActionId);

    private static void PatchSnapshot(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot!;
        workItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v9",
            States = snapshot.States,
            Transitions = snapshot.Transitions.Append(s_newTransition).ToList()
        };
        workItem.TemplateVersion = "v9";
    }
}
