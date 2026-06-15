using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Back-fills the SLA clock for re-accreditation work items that are in
/// <c>duly-made</c> state but have a <c>null</c> <see cref="WorkItem.SlaClock"/>.
///
/// This situation arises when an item was auto-transitioned to <c>duly-made</c>
/// by a version of <see cref="ReAccreditationDulyMadeHook"/> that pre-dated the
/// SLA-clock change, or by <see cref="ReAccreditationDulyMadeSnapshotMigration"/>
/// running against an item that was already in <c>duly-made</c> state.
///
/// <see cref="WorkItem.LastModifiedAt"/> is used as <c>StartedAt</c> because that
/// timestamp was written by the hook at the time it performed the state transition.
///
/// Idempotent: items that already have a non-null <see cref="WorkItem.SlaClock"/>
/// are skipped.
/// </summary>
internal sealed class ReAccreditationDulyMadeSlaClockBackfillMigration(
    ILogger<ReAccreditationDulyMadeSlaClockBackfillMigration> logger,
    TimeProvider? timeProvider = null) : IWorkItemMigration
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string Name => "ReAccreditation: backfill SLA clock for duly-made items with null clock";

    public async Task ApplyAsync(IWorkItemPersistence persistence, CancellationToken cancellationToken)
    {
        var backfilled = 0;
        var skipped = 0;
        var page = 1;
        const int pageSize = WorkItemQuery.MaxPageSize;

        while (true)
        {
            var result = await persistence.QueryAsync(
                new WorkItemQuery(
                    TypeIds: [ReAccreditationType.Id],
                    StateIds: ["duly-made"],
                    Page: page,
                    PageSize: pageSize,
                    IncludeArchived: false),
                cancellationToken);

            foreach (var candidate in result.Items)
            {
                // QueryAsync does not project SlaClock — fetch the full document.
                var full = await persistence.GetByIdAsync(candidate.Id, cancellationToken);
                if (full is null || full.SlaClock is not null)
                {
                    skipped++;
                    continue;
                }

                var now = _timeProvider.GetUtcNow().UtcDateTime;
                full.SlaClock = new WorkItemSlaClock { StartedAt = full.LastModifiedAt };
                full.AuditLog.Add(new WorkItemAuditEntry
                {
                    Action = "sla-clock-started",
                    ActionDisplayName = "SLA clock started",
                    CreatedAt = now,
                    CreatedBy = "migration",
                    CreatedByName = "Migration",
                    Details = new Dictionary<string, string?>
                    {
                        ["startedAt"] = full.LastModifiedAt.ToString("O"),
                        ["targetDays"] = new WorkItemSlaClock().TargetDuration.TotalDays.ToString()
                    }
                });

                try
                {
                    await persistence.ReplaceAsync(full, cancellationToken);
                    backfilled++;
                }
                catch (WorkItemConcurrencyException)
                {
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
            "Migration '{Name}' complete: {Backfilled} SLA clocks backfilled, {Skipped} already current.",
            Name, backfilled, skipped);
    }
}
