using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Adds the <c>queried → queried</c> <c>sla-extend</c> transition (RA-351) to
/// the frozen <see cref="WorkItemTemplateSnapshot"/> of every re-accreditation
/// work item and bumps <see cref="WorkItem.TemplateVersion"/> from <c>v12</c>
/// to <c>v13</c>.
///
/// <see cref="WorkItemService"/> matches an action against the work item's own
/// frozen snapshot, not the live <see cref="ReAccreditationType"/> (the
/// snapshot is captured once, at submission). Without this migration, every
/// re-accreditation work item submitted before this deploy — including any
/// already sitting in <c>queried</c> today — has no way to Extend/Override the
/// SLA from <c>queried</c>: adding the transition to the live type only
/// benefits work items submitted after the deploy. This mirrors
/// <see cref="ReAccreditationWithdrawQuerySnapshotMigration"/>'s v8→v9
/// precedent.
///
/// The marker is state-qualified, not just the action id: a v12 snapshot
/// already carries an <c>sla-extend</c> transition (the assessment-in-progress
/// self-loop), so this migration keys off an <c>sla-extend</c> transition
/// whose <see cref="WorkItemTransition.FromStateId"/> is <c>queried</c>. The
/// migration is idempotent: items whose snapshot already contains that
/// transition are skipped.
/// </summary>
internal sealed class ReAccreditationSlaExtendQuerySnapshotMigration(
    ILogger<ReAccreditationSlaExtendQuerySnapshotMigration> logger
) : IWorkItemMigration
{
    /// <summary>
    /// Action id of the transition this migration adds. Kept in sync with the
    /// literal <c>sla-extend</c> id declared in <see cref="ReAccreditationType"/>.
    /// Not sufficient on its own as a presence marker — see
    /// <see cref="FromStateId"/>.
    /// </summary>
    private const string MarkerActionId = "sla-extend";

    /// <summary>
    /// From-state that distinguishes the new v13 transition from the v12
    /// assessment-in-progress <c>sla-extend</c> self-loop that a v12 snapshot
    /// already carries.
    /// </summary>
    private const string FromStateId = "queried";

    private static readonly WorkItemTransition s_newTransition = new(
        "sla-extend",
        "Extend SLA",
        "queried",
        "queried"
    );

    public string Name =>
        "ReAccreditation: add queried sla-extend transition to snapshot (v12 → v13)";

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
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug(
                            "Concurrency conflict on work item {Id}; skipping — another instance already migrated it.",
                            full.Id
                        );
                    }
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

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Migration '{Name}' complete: {Migrated} updated, {Skipped} already current.",
                Name,
                migrated,
                skipped
            );
        }
    }

    private static bool NeedsMigration(WorkItem workItem) =>
        workItem.TemplateSnapshot is not null
        && workItem.TemplateSnapshot.Transitions.All(t =>
            t.ActionId != MarkerActionId || t.FromStateId != FromStateId
        );

    private static void PatchSnapshot(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot!;
        workItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v13",
            States = snapshot.States,
            Transitions = snapshot.Transitions.Append(s_newTransition).ToList(),
        };
        workItem.TemplateVersion = "v13";
    }
}
