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
    ILogger<ReAccreditationDisplayNameSnapshotMigration> logger)
    : ReAccreditationSnapshotMigrationBase(logger)
{
    /// <summary>
    /// State id → new AC06 display label. These target labels are deliberately
    /// duplicated here rather than read from <see cref="ReAccreditationType"/>:
    /// a snapshot migration must freeze the exact labels its version produces so
    /// a future rename of the live type can never retroactively change what the
    /// v9 migration wrote. Kept in sync with the four renamed states declared in
    /// <see cref="ReAccreditationType"/>. Ordinal-ignore-case because state ids
    /// are compared case-insensitively across the engine.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> s_displayNameRenames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["submitted"] = "Not started",
            ["assessment-in-progress"] = "Updated",
            ["approved"] = "Granted",
            ["rejected"] = "Refused",
        };

    public override string Name =>
        "ReAccreditation: rename state display labels in snapshot to AC06 set (v8 → v9)";

    protected override bool NeedsMigration(WorkItem workItem) =>
        workItem.TemplateSnapshot is not null &&
        workItem.TemplateSnapshot.States.Any(s =>
            s_displayNameRenames.TryGetValue(s.Id, out var newName) &&
            !string.Equals(s.DisplayName, newName, StringComparison.Ordinal));

    protected override void PatchSnapshot(WorkItem workItem)
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
            Transitions = snapshot.Transitions
        };
        workItem.TemplateVersion = "v9";
    }
}
