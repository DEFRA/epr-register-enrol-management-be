using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Strips the deprecated <c>duly-make</c> transition from the
/// <see cref="WorkItemTemplateSnapshot"/> of all re-accreditation work items
/// and bumps <see cref="WorkItem.TemplateVersion"/> from <c>v4</c> to
/// <c>v5</c>.
///
/// RA-410: this migration used to also auto-advance a <c>submitted</c> item
/// whose checklist was fully ticked into <c>duly-made</c>, standing in for the
/// auto-transition hook that never fired for it. With the task framework gone
/// there is no checklist to read, and RA-316 had already made duly making
/// depend on a regulator-entered payment date that a migration cannot invent
/// without anchoring the 12-week SLA to a fiction. Such items are now left in
/// <c>submitted</c> and presented with the "Duly make" call to action like any
/// other, which is the correct destination for them.
///
/// The migration is idempotent: items whose snapshot no longer contains
/// <c>duly-make</c> are skipped.
/// </summary>
internal sealed class ReAccreditationDulyMadeSnapshotMigration(
    ILogger<ReAccreditationDulyMadeSnapshotMigration> logger,
    TimeProvider? timeProvider = null) : IWorkItemMigration
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string Name => "ReAccreditation: remove duly-make transition from snapshot (v4 → v5)";

    public async Task ApplyAsync(IWorkItemPersistence persistence, CancellationToken cancellationToken)
    {
        var migrated = 0;
        var autoTransitioned = 0;
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
            "Migration '{Name}' complete: {Migrated} updated ({AutoTransitioned} auto-transitioned to duly-made), {Skipped} already current.",
            Name, migrated, autoTransitioned, skipped);
    }

    /// <summary>
    /// RA-316: the presence of <c>duly-make</c> is no longer sufficient on its
    /// own. <see cref="ReAccreditationDulyMakeSnapshotMigration"/> deliberately
    /// re-adds that transition at v11, so this migration must additionally
    /// establish that the item really is a pre-v5 straggler. Without the version
    /// gate the two migrations would fight on every boot — this one stripping
    /// what the other had just restored, two pointless writes per item per
    /// start-up, and a window in which nothing could be duly made.
    ///
    /// Versions are compared numerically rather than by string equality so a
    /// v1/v2/v3 item (should any survive) is still caught.
    /// </summary>
    private static bool NeedsMigration(WorkItem workItem) =>
        workItem.TemplateSnapshot is not null &&
        workItem.TemplateSnapshot.Transitions.Any(t => t.ActionId == "duly-make") &&
        IsPreV5(workItem.TemplateSnapshot.TemplateVersion);

    /// <summary>
    /// True when <paramref name="templateVersion"/> is a "v&lt;n&gt;" string with
    /// n &lt; 5. An unrecognised or absent version is treated as pre-v5: the only
    /// snapshots that never carried a parseable version predate versioning
    /// altogether, and they are exactly the ones this migration exists for.
    /// </summary>
    private static bool IsPreV5(string? templateVersion)
    {
        if (string.IsNullOrWhiteSpace(templateVersion))
        {
            return true;
        }

        var digits = templateVersion.TrimStart('v', 'V');
        return !int.TryParse(digits, out var version) || version < 5;
    }

    private static void PatchSnapshot(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot!;
        workItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v5",
            States = snapshot.States,
            Transitions = snapshot.Transitions.Where(t => t.ActionId != "duly-make").ToList()
        };
        workItem.TemplateVersion = "v5";
    }
}
