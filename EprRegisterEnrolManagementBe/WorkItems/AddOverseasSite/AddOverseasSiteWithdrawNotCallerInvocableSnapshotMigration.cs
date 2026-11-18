using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;

/// <summary>
/// RA-495 review: brings the frozen <see cref="WorkItemTemplateSnapshot"/> of
/// every add-ORS work item onto the v2 rules and sets
/// <see cref="WorkItem.TemplateVersion"/> to <c>v2</c>: every
/// <c>withdraw*</c> declaration (<c>withdraw</c>,
/// <c>-during-duly-made</c>, <c>-during-assessment</c> from both
/// <c>assessment-in-progress</c> and <c>assessment-started</c>,
/// <c>-during-decision</c>, <c>-during-query</c>, <c>-during-updated</c>)
/// becomes <c>CallerInvocable: false</c>.
///
/// The generic action endpoint checks <c>CallerInvocable</c> against the
/// work item's own frozen snapshot, so without this migration an open v1
/// item could still be withdrawn through
/// <c>POST /work-items/{id}/actions/withdraw*</c>. Withdraw is
/// re-accreditation-only: nothing tells the operator journey, so the site
/// would stay locked there.
///
/// Only the flag changes: action ids, display names, from- and to-states are
/// kept as each item's snapshot has them. Writes only
/// <see cref="WorkItem.TemplateSnapshot"/> and
/// <see cref="WorkItem.TemplateVersion"/>; it never changes the state,
/// assignee, payload, SLA clock, notes or audit log. Idempotent: an item with
/// no caller-invocable <c>withdraw*</c> declaration (or no snapshot, which the
/// engine resolves to the live v2 type) is skipped.
///
/// A small migration of its own rather than
/// <c>ReAccreditationSnapshotMigrationBase</c>, which is re-accreditation's
/// (a module must not depend on another module). Like it, it decides from the
/// projected candidate, re-reads the full audit-bearing document before
/// saving (the query projection omits <see cref="WorkItem.AuditLog"/> and
/// <see cref="WorkItem.Notes"/>), re-checks it, and treats a concurrency
/// conflict as another instance having migrated the item already.
/// </summary>
internal sealed class AddOverseasSiteWithdrawNotCallerInvocableSnapshotMigration(
    ILogger<AddOverseasSiteWithdrawNotCallerInvocableSnapshotMigration> logger
) : IWorkItemMigration
{
    private const string TargetVersion = "v2";

    // Frozen as literals, like the re-accreditation snapshot migrations: a
    // later change to the live type must not silently change what this v2
    // migration writes.
    private static readonly HashSet<string> s_withdrawActionIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "withdraw",
        "withdraw-during-duly-made",
        "withdraw-during-assessment",
        "withdraw-during-decision",
        "withdraw-during-query",
        "withdraw-during-updated",
    };

    public string Name =>
        "AddOverseasSite: make the withdraw* transitions not caller-invocable in snapshot (v1 → v2)";

    public async Task ApplyAsync(IWorkItemPersistence persistence, CancellationToken cancellationToken)
    {
        var migrated = 0;
        var skipped = 0;

        await WorkItemMigrationPaging.VisitAllAsync(
            persistence,
            AddOverseasSiteType.Id,
            async (candidate, ct) =>
            {
                if (!NeedsMigration(candidate))
                {
                    skipped++;
                    return;
                }

                // Re-read before saving: the candidate omits AuditLog/Notes,
                // and another instance may have migrated it since the query.
                var full = await persistence.GetByIdAsync(candidate.Id, ct);
                if (full is null || !NeedsMigration(full))
                {
                    skipped++;
                    return;
                }

                PatchSnapshot(full);

                try
                {
                    await persistence.ReplaceAsync(full, ct);
                    migrated++;
                }
                catch (WorkItemConcurrencyException ex)
                {
                    logger.LogDebug(
                        ex,
                        "Concurrency conflict on work item {Id}; skipping — another instance already migrated it.",
                        full.Id
                    );
                    skipped++;
                }
            },
            cancellationToken
        );

        logger.LogInformation(
            "Migration '{Name}' complete: {Migrated} updated, {Skipped} already current.",
            Name,
            migrated,
            skipped
        );
    }

    private static bool NeedsMigration(WorkItem workItem) =>
        workItem.TemplateSnapshot is { } snapshot && snapshot.Transitions.Any(IsCallerInvocableWithdraw);

    private static void PatchSnapshot(WorkItem workItem)
    {
        var snapshot = workItem.TemplateSnapshot!;

        workItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = TargetVersion,
            States = snapshot.States,
            Transitions = snapshot
                .Transitions.Select(t => IsCallerInvocableWithdraw(t) ? t with { CallerInvocable = false } : t)
                .ToList(),
        };
        workItem.TemplateVersion = TargetVersion;
    }

    private static bool IsCallerInvocableWithdraw(WorkItemTransition transition) =>
        transition.CallerInvocable && s_withdrawActionIds.Contains(transition.ActionId);
}
