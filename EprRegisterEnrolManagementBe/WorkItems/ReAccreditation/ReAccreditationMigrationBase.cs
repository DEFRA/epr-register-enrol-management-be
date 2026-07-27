using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Template-method base for the re-accreditation <see cref="IWorkItemMigration"/>
/// family. Owns the one thing every migration copy-pasted verbatim: the paged
/// scan of the work-item collection, the re-read of the full audit-bearing
/// document before saving, the optimistic-concurrency swallow and the
/// completion log. Subclasses provide only the per-item decision
/// (<see cref="TryMigrate"/>) and, where they differ, the page query, the
/// cheap candidate pre-filter and the completion log line.
///
/// The re-read (<see cref="IWorkItemPersistence.GetByIdAsync"/>) is
/// deliberate and load-bearing: <see cref="IWorkItemPersistence.QueryAsync"/>
/// projects a lightweight candidate that omits <see cref="WorkItem.AuditLog"/>
/// and <see cref="WorkItem.Notes"/>, so saving that candidate would wipe audit
/// history. Every migration mutates and saves the full document, never the
/// query candidate.
/// </summary>
internal abstract class ReAccreditationMigrationBase : IWorkItemMigration
{
    protected ReAccreditationMigrationBase(ILogger logger) => Logger = logger;

    /// <summary>Logger, category-typed by the concrete subclass.</summary>
    protected ILogger Logger { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    public async Task ApplyAsync(IWorkItemPersistence persistence, CancellationToken cancellationToken)
    {
        OnRunStarting();

        var migrated = 0;
        var skipped = 0;
        var page = 1;
        const int pageSize = WorkItemQuery.MaxPageSize;

        while (true)
        {
            var result = await persistence.QueryAsync(BuildPageQuery(page, pageSize), cancellationToken);

            foreach (var candidate in result.Items)
            {
                if (!ShouldConsider(candidate))
                {
                    skipped++;
                    continue;
                }

                // QueryAsync omits AuditLog/Notes — fetch the full document before saving
                // so we do not accidentally wipe audit history on ReplaceAsync.
                var full = await persistence.GetByIdAsync(candidate.Id, cancellationToken);
                if (full is null || !TryMigrate(full))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    await persistence.ReplaceAsync(full, cancellationToken);
                    migrated++;
                }
                catch (WorkItemConcurrencyException)
                {
                    // Another instance migrated this item concurrently; it is already up to date.
                    Logger.LogDebug(
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

        LogCompletion(migrated, skipped);
    }

    /// <summary>
    /// Called once at the start of every run, before any page is queried.
    /// Lets a subclass reset per-run scratch state (e.g. an extra counter that
    /// feeds its completion log) so a repeated invocation on the same instance
    /// behaves exactly like a fresh one. Default: no-op.
    /// </summary>
    protected virtual void OnRunStarting() { }

    /// <summary>
    /// Builds the query for one page of candidates. Default: every
    /// re-accreditation work item in any state, including archived.
    /// </summary>
    protected virtual WorkItemQuery BuildPageQuery(int page, int pageSize) =>
        new(
            TypeIds: [ReAccreditationType.Id],
            Page: page,
            PageSize: pageSize,
            IncludeArchived: true);

    /// <summary>
    /// Cheap pre-filter run against the projected query candidate (no
    /// AuditLog/Notes). Returning <c>false</c> skips the item without the
    /// <see cref="IWorkItemPersistence.GetByIdAsync"/> re-read. Default:
    /// always re-read — back-fills inspect fields the projection omits, so
    /// they cannot decide from the candidate alone.
    /// </summary>
    protected virtual bool ShouldConsider(WorkItem candidate) => true;

    /// <summary>
    /// Applies the migration to the full, audit-bearing document. Returns
    /// <c>true</c> if the document was changed and should be saved, or
    /// <c>false</c> to leave it untouched (already current / nothing to do).
    /// </summary>
    protected abstract bool TryMigrate(WorkItem full);

    /// <summary>
    /// Writes the completion log line. Default reports the shared
    /// "updated / already current" tally; subclasses override where their
    /// wording or extra counters differ.
    /// </summary>
    protected virtual void LogCompletion(int migrated, int skipped) =>
        Logger.LogInformation(
            "Migration '{Name}' complete: {Migrated} updated, {Skipped} already current.",
            Name, migrated, skipped);
}
