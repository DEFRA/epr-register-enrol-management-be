using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Renames the display labels of four states in the frozen
/// <see cref="WorkItemTemplateSnapshot"/> of every re-accreditation work item
/// to the RA-324 (AC06) "Applications" set — <c>submitted</c> → "Not started",
/// <c>assessment-in-progress</c> → "Updated", <c>approved</c> → "Granted",
/// <c>rejected</c> → "Refused" — and bumps <see cref="WorkItem.TemplateVersion"/>
/// from <c>v8</c> to <c>v9</c>.
///
/// <see cref="WorkItemService"/> resolves an item's template from its own frozen
/// snapshot, not the live <see cref="ReAccreditationType"/> (the snapshot is
/// captured once, at submission). Without this migration every re-accreditation
/// work item submitted before this deploy would keep rendering the old
/// "Submitted"/"Assessment in progress"/"Approved"/"Rejected" labels: renaming
/// the live type only reaches items submitted after the deploy.
///
/// Only state <c>DisplayName</c>s change — no state <c>Id</c>, transition or
/// task is touched (the ids are the wire contract), and, like the preceding
/// snapshot migrations, this never changes any work item's current
/// <c>StateId</c>: an approved item stays approved, it just reads "Granted".
///
/// The migration is idempotent: an item whose snapshot already carries every
/// renamed state's target label is skipped.
/// </summary>
internal sealed class ReAccreditationDisplayNameSnapshotMigration(
    ILogger<ReAccreditationDisplayNameSnapshotMigration> logger) : IWorkItemMigration
{
    /// <summary>
    /// State id → new AC06 display label. Kept in sync with the four renamed
    /// states declared in <see cref="ReAccreditationType"/>. Ordinal-ignore-case
    /// because state ids are compared case-insensitively across the engine.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> s_displayNameRenames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["submitted"] = "Not started",
            ["assessment-in-progress"] = "Updated",
            ["approved"] = "Granted",
            ["rejected"] = "Refused",
        };

    public string Name =>
        "ReAccreditation: rename state display labels in snapshot to AC06 set (v8 → v9)";

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
        workItem.TemplateSnapshot.States.Any(s =>
            s_displayNameRenames.TryGetValue(s.Id, out var newName) &&
            !string.Equals(s.DisplayName, newName, StringComparison.Ordinal));

    private static void PatchSnapshot(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot!;

        var renamedStates = snapshot.States
            .Select(s =>
                s_displayNameRenames.TryGetValue(s.Id, out var newName)
                    ? s with { DisplayName = newName }
                    : s)
            .ToList();

        workItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v9",
            States = renamedStates,
            Transitions = snapshot.Transitions,
            TasksByState = snapshot.TasksByState
        };
        workItem.TemplateVersion = "v9";
    }
}
