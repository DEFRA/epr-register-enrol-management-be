using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-410: marks <c>submit-for-decision</c> and <c>reject</c> as
/// <see cref="WorkItemTransition.CallerInvocable"/> <c>false</c> on the frozen
/// <see cref="WorkItemTemplateSnapshot"/> of every existing re-accreditation
/// work item, and bumps <see cref="WorkItem.TemplateVersion"/> from <c>v11</c>
/// to <c>v12</c>.
///
/// <see cref="WorkItemService"/> builds a work item's available actions from
/// its own frozen snapshot, not the live <see cref="ReAccreditationType"/>.
/// Without this migration an in-flight application would keep advertising the
/// old two-step decision path — a "Submit for decision" control whose only
/// effect is to park it in <c>awaiting-decision</c>, exactly the intermediate
/// state RA-410 exists to stop users seeing — while a freshly submitted one
/// offered the single "Log decision" call to action. Two different decision
/// journeys running side by side is worse than either.
///
/// Nothing else about the snapshot changes. Task lists are not stripped here
/// because they no longer need to be: <see cref="WorkItemTemplateSnapshot"/>
/// stopped modelling them, so a stale <c>tasksByState</c> is ignored on read
/// and dropped on the next write. State ids, action ids and target states are
/// untouched — <c>reject</c> stays <c>reject</c> and still lands on
/// <c>rejected</c>, so stored audit entries and notification templates that
/// name them keep resolving.
///
/// The migration is idempotent: an item whose snapshot already declares both
/// transitions non-caller-invocable is skipped.
/// </summary>
internal sealed class ReAccreditationDecisionSnapshotMigration(
    ILogger<ReAccreditationDecisionSnapshotMigration> logger
) : IWorkItemMigration
{
    private const string TargetVersion = "v12";

    /// <summary>
    /// The action ids whose <c>CallerInvocable</c> flag this migration clears.
    /// Kept in sync with <see cref="ReAccreditationType"/>: an item migrated
    /// with different flags would be judged by different rules than a freshly
    /// submitted one.
    /// </summary>
    private static readonly IReadOnlySet<string> s_serverResolvedActionIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "submit-for-decision", "reject" };

    public string Name =>
        "ReAccreditation: make submit-for-decision and reject server-resolved in snapshot (v11 → v12)";

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
        && workItem.TemplateSnapshot.Transitions.Any(t =>
            s_serverResolvedActionIds.Contains(t.ActionId) && t.CallerInvocable
        );

    private static void PatchSnapshot(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot!;
        workItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = TargetVersion,
            States = snapshot.States,
            Transitions = snapshot
                .Transitions.Select(t =>
                    s_serverResolvedActionIds.Contains(t.ActionId)
                        ? t with { CallerInvocable = false }
                        : t
                )
                .ToList()
        };
        workItem.TemplateVersion = TargetVersion;
    }
}
