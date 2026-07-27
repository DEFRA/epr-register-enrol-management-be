using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Strips the deprecated <c>duly-make</c> transition from the
/// <see cref="WorkItemTemplateSnapshot"/> of all re-accreditation work items
/// and bumps <see cref="WorkItem.TemplateVersion"/> from <c>v4</c> to
/// <c>v5</c>.
///
/// For items in the <c>submitted</c> state whose tasks are all already
/// complete, also applies the <c>submitted → duly-made</c> state transition
/// that would previously have required a manual "Mark as duly made" action,
/// since the auto-transition hook (<see cref="ReAccreditationDulyMadeHook"/>)
/// never fired for them. No notification email is sent during migration.
///
/// The migration is idempotent: items whose snapshot no longer contains
/// <c>duly-make</c> are skipped.
/// </summary>
internal sealed class ReAccreditationDulyMadeSnapshotMigration(
    ILogger<ReAccreditationDulyMadeSnapshotMigration> logger,
    TimeProvider? timeProvider = null)
    : ReAccreditationMigrationBase(logger)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private int _autoTransitioned;

    public override string Name => "ReAccreditation: remove duly-make transition from snapshot (v4 → v5)";

    protected override void OnRunStarting() => _autoTransitioned = 0;

    protected override bool ShouldConsider(WorkItem candidate) => NeedsMigration(candidate);

    protected override bool TryMigrate(WorkItem full)
    {
        // Re-check against the full document: another instance may have
        // migrated it between the query and the re-read.
        if (!NeedsMigration(full))
        {
            return false;
        }

        PatchSnapshot(full);

        if (full.StateId == "submitted" && AllTasksComplete(full, "submitted"))
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            full.StateId = "duly-made";
            full.SlaClock = new WorkItemSlaClock { StartedAt = now };
            full.AuditLog.Add(new WorkItemAuditEntry
            {
                Action = "action-applied",
                ActionDisplayName = "Action applied",
                CreatedAt = now,
                CreatedBy = "migration",
                CreatedByName = "Migration",
                Details = new Dictionary<string, string?>
                {
                    ["actionId"] = "duly-make",
                    ["fromStateId"] = "submitted",
                    ["toStateId"] = "duly-made"
                }
            });
            full.AuditLog.Add(new WorkItemAuditEntry
            {
                Action = "sla-clock-started",
                ActionDisplayName = "SLA clock started",
                CreatedAt = now,
                CreatedBy = "migration",
                CreatedByName = "Migration",
                Details = new Dictionary<string, string?>
                {
                    ["startedAt"] = now.ToString("O"),
                    ["targetDays"] = new WorkItemSlaClock().TargetDuration.TotalDays.ToString()
                }
            });
            _autoTransitioned++;
        }

        return true;
    }

    protected override void LogCompletion(int migrated, int skipped) =>
        Logger.LogInformation(
            "Migration '{Name}' complete: {Migrated} updated ({AutoTransitioned} auto-transitioned to duly-made), {Skipped} already current.",
            Name, migrated, _autoTransitioned, skipped);

    private static bool NeedsMigration(WorkItem workItem) =>
        workItem.TemplateSnapshot is not null &&
        workItem.TemplateSnapshot.Transitions.Any(t => t.ActionId == "duly-make");

    private static void PatchSnapshot(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot!;
        workItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v5",
            States = snapshot.States,
            Transitions = snapshot.Transitions.Where(t => t.ActionId != "duly-make").ToList(),
            TasksByState = snapshot.TasksByState
        };
        workItem.TemplateVersion = "v5";
    }

    private static bool AllTasksComplete(WorkItem workItem, string stateId)
    {
        var tasks = workItem.TemplateSnapshot!.GetTasksForState(stateId);
        if (tasks.Count == 0)
        {
            return false;
        }

        return tasks.All(t => GetTaskStatus(workItem, stateId, t.Id) == WorkItemTaskStatus.Completed);
    }

    private static WorkItemTaskStatus GetTaskStatus(WorkItem workItem, string stateId, string taskId)
    {
        if (workItem.TaskStatusesByState.TryGetValue(stateId, out var statuses) &&
            statuses.TryGetValue(taskId, out var status))
        {
            return status;
        }

        if (workItem.CompletedTaskIdsByState.TryGetValue(stateId, out var bucket) &&
            bucket.Contains(taskId))
        {
            return WorkItemTaskStatus.Completed;
        }

        return WorkItemTaskStatus.NotStarted;
    }
}
