namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Base for the "patch the frozen template snapshot" migrations. On top of
/// <see cref="WorkItemMigrationBase"/> it wires the two hooks every such
/// migration shares: a <see cref="NeedsMigration"/> predicate (used both as
/// the cheap candidate pre-filter <em>and</em> re-checked against the full
/// document after the re-read, guarding against a concurrent migration) and a
/// <see cref="PatchSnapshot"/> body. Concrete snapshot migrations therefore
/// shrink to exactly those two members plus their
/// <see cref="WorkItemMigrationBase.Name"/>.
///
/// RA-495 review: lifted from re-accreditation's own base, as
/// <see cref="WorkItemMigrationBase"/> was.
///
/// The candidate pre-filter is safe here because
/// <see cref="WorkItem.TemplateSnapshot"/> is included in the query
/// projection, so <see cref="NeedsMigration"/> can decide from the candidate
/// alone and skip the re-read for already-current items.
/// </summary>
internal abstract class WorkItemSnapshotMigrationBase(string typeId, ILogger logger)
    : WorkItemMigrationBase(typeId, logger)
{
    /// <summary>
    /// Does this work item still need migrating? Evaluated against the
    /// snapshot only, so it holds for both the projected candidate and the
    /// re-read full document.
    /// </summary>
    protected abstract bool NeedsMigration(WorkItem workItem);

    /// <summary>Rewrites the frozen snapshot to the new template version.</summary>
    protected abstract void PatchSnapshot(WorkItem workItem);

    protected sealed override bool ShouldConsider(WorkItem candidate) => NeedsMigration(candidate);

    protected sealed override bool TryMigrate(WorkItem full)
    {
        // Re-check against the full document: another instance may have
        // migrated it between the query and the re-read.
        if (!NeedsMigration(full))
        {
            return false;
        }

        PatchSnapshot(full);
        return true;
    }
}
