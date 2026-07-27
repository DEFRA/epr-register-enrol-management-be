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
    TimeProvider? timeProvider = null)
    : ReAccreditationMigrationBase(logger)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public override string Name => "ReAccreditation: backfill SLA clock for duly-made items with null clock";

    protected override WorkItemQuery BuildPageQuery(int page, int pageSize) =>
        new(
            TypeIds: [ReAccreditationType.Id],
            StateIds: ["duly-made"],
            Page: page,
            PageSize: pageSize,
            IncludeArchived: false);

    protected override bool TryMigrate(WorkItem full)
    {
        if (full.SlaClock is not null)
        {
            return false;
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

        return true;
    }

    protected override void LogCompletion(int migrated, int skipped) =>
        Logger.LogInformation(
            "Migration '{Name}' complete: {Backfilled} SLA clocks backfilled, {Skipped} already current.",
            Name, migrated, skipped);
}
