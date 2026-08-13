using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-316: reinstates the <c>duly-make</c> transition on the frozen
/// <see cref="WorkItemTemplateSnapshot"/> of every re-accreditation work item
/// and bumps <see cref="WorkItem.TemplateVersion"/> from <c>v10</c> to
/// <c>v11</c>.
///
/// <see cref="WorkItemService"/> matches an action against the work item's own
/// frozen snapshot, not the live <see cref="ReAccreditationType"/>. Without this
/// migration every existing work item would be stranded twice over: it would
/// have no <c>duly-make</c> transition (so the new "Duly make" call to action
/// could not be honoured, and — with the auto-transition hook deleted — nothing
/// else could move it out of <c>submitted</c> either). Adding the transition to
/// the live type alone only helps work items submitted after the deploy.
///
/// Note this migration REVERSES part of
/// <see cref="ReAccreditationDulyMadeSnapshotMigration"/>, which strips
/// <c>duly-make</c> as its v4 → v5 step. That is safe because that migration is
/// now gated on the item's template version being pre-v5, so it cannot see —
/// and therefore cannot re-strip — the transition this one re-adds. Without that
/// gate the two would fight on every boot, each undoing the other and leaving a
/// window in which no item could be duly made.
///
/// The migration is idempotent: items whose snapshot already carries
/// <c>duly-make</c> are skipped.
/// </summary>
internal sealed class ReAccreditationDulyMakeSnapshotMigration(
    ILogger<ReAccreditationDulyMakeSnapshotMigration> logger
) : IWorkItemMigration
{
    /// <summary>
    /// Kept in sync with the <c>duly-make</c> transition declared in
    /// <see cref="ReAccreditationType"/>. The two must agree: an item migrated
    /// with different flags would be judged by different rules than a freshly
    /// submitted one.
    /// </summary>
    private static readonly WorkItemTransition s_dulyMakeTransition = new(
        "duly-make",
        "Duly make",
        "submitted",
        "duly-made",
        CallerInvocable: false
    );

    private const string TargetVersion = "v11";

    public string Name =>
        "ReAccreditation: reinstate duly-make transition in snapshot (v10 → v11)";

    public async Task ApplyAsync(
        IWorkItemPersistence persistence,
        CancellationToken cancellationToken
    )
    {
        var migrated = 0;
        var skipped = 0;
        var failed = 0;
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
                // One unreadable document must not abandon the batch (epr-dtkw).
                // Without this, a single document whose snapshot tripped
                // NeedsMigration threw straight out of ApplyAsync; the host
                // logged "failed; continuing startup. Will retry on next boot"
                // and the next boot met the same document. Every work item
                // behind it in the page was left unmigrated, permanently — the
                // duly-make transition never reached their snapshots, so duly
                // making refused them with InvalidTransition. Skipping the one
                // document and logging it keeps the other thousands migrating,
                // and turns a silent permanent stall into a named record to
                // investigate.
                try
                {
                    if (!NeedsMigration(candidate))
                    {
                        skipped++;
                        continue;
                    }

                    // QueryAsync omits AuditLog/Notes — fetch the full document
                    // before saving so we do not wipe audit history on ReplaceAsync.
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
                        // Another instance migrated this item concurrently; it is
                        // already up to date.
                        logger.LogDebug(
                            "Concurrency conflict on work item {Id}; skipping — another instance "
                                + "already migrated it.",
                            full.Id
                        );
                        skipped++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Deliberately broad, and deliberately not rethrown. The
                    // point is that NOTHING about one malformed document may
                    // stop the others migrating; narrowing this to the
                    // exception we happen to have seen would just move the
                    // stall to the next unanticipated shape. Cancellation still
                    // propagates — a shutdown is not a document problem.
                    failed++;
                    logger.LogError(
                        ex,
                        "Migration '{Name}' could not process work item {Id}; skipping it and "
                            + "continuing with the rest of the batch.",
                        Name,
                        candidate.Id
                    );
                }
            }

            var processed = (long)(page - 1) * pageSize + result.Items.Count;
            if (processed >= result.TotalCount)
            {
                break;
            }

            page++;
        }

        // `failed` is reported even when zero: a migration that silently
        // skips documents reads as "complete" and hides a permanent gap.
        logger.LogInformation(
            "Migration '{Name}' complete: {Migrated} updated, {Skipped} already current, "
                + "{Failed} skipped after errors.",
            Name,
            migrated,
            skipped,
            failed
        );
    }

    /// <summary>
    /// Tests the two conditions independently rather than trusting the stored
    /// version string. An item that somehow has one half applied and not the
    /// other — a crash between two deploys, a hand-edited document — is still
    /// picked up and finished off.
    /// </summary>
    private static bool NeedsMigration(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot;
        if (snapshot is null)
        {
            // Nothing to patch. Such an item resolves its template from the live
            // registry (see WorkItemEngineRules.ResolveTemplate), so it already
            // sees v11 rules and is not stranded.
            return false;
        }

        return !snapshot.Transitions.Any(t =>
            string.Equals(t.ActionId, s_dulyMakeTransition.ActionId, StringComparison.OrdinalIgnoreCase)
        );
    }

    private static void PatchSnapshot(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot!;

        var transitions = snapshot
            .Transitions.Where(t =>
                !string.Equals(
                    t.ActionId,
                    s_dulyMakeTransition.ActionId,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .Append(s_dulyMakeTransition)
            .ToList();

        workItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = TargetVersion,
            States = snapshot.States,
            Transitions = transitions,
        };
        workItem.TemplateVersion = TargetVersion;

        // Deliberately NOT touched: the item's state. An item sitting in
        // 'submitted' is NOT auto-advanced to 'duly-made' here, even though the
        // old hook would have advanced one whose checklist was complete. Duly
        // making requires a payment date that only the regulator can supply,
        // and inventing one (today's date, the submission date) would anchor
        // the 12-week SLA to a fiction. Such items simply present the "Duly
        // make" call to action like any other — which is the correct
        // destination for them.
    }
}
