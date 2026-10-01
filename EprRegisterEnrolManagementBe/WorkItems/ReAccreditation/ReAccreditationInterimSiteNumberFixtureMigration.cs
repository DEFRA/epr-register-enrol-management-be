using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-603: moves the <see cref="ReAccreditationSeeder.OrsInterimAuthoritySeedKey"/> fixture's
/// interim site numbers from <c>INT-001</c>/<c>INT-002</c> to the backend's <c>001</c>/<c>002</c>
/// format, matching what <see cref="ReAccreditationSeeder"/> now writes.
///
/// <see cref="IWorkItemPersistence.CreateIfAbsentAsync"/> inserts by deterministic id and never
/// updates, so an environment that seeded before the seeder changed keeps the old numbers, and
/// mgmt-tests - which expects the new ones - fails there while a from-scratch CI run stays green.
/// A new seed key would not help: it inserts a second fixture under the same organisation name,
/// which mgmt-tests relies on being unique.
///
/// Scoped the same way as <see cref="ReAccreditationExporterFixtureBackfillMigration"/>: to the
/// one known fixture id, and only where a number still holds its exact old seeded value. A number
/// an operator was really allocated is never in reach.
///
/// Rewrites the singular <c>interimSite</c> mirror and any <c>interimSites</c> entry alike, so
/// it is correct whether or not <see cref="ReAccreditationInterimSitesBackfillMigration"/> has
/// already copied the mirror into the list. Idempotent: once renumbered there is nothing left to
/// match.
/// </summary>
internal sealed class ReAccreditationInterimSiteNumberFixtureMigration(
    ILogger<ReAccreditationInterimSiteNumberFixtureMigration> logger,
    TimeProvider? timeProvider = null
) : IWorkItemMigration
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private static readonly Dictionary<string, string> s_renumbering = new()
    {
        ["INT-001"] = "001",
        ["INT-002"] = "002",
    };

    public string Name =>
        "ReAccreditation: renumber the ors-interim-authority seed fixture's interim sites (RA-603)";

    public async Task ApplyAsync(
        IWorkItemPersistence persistence,
        CancellationToken cancellationToken
    )
    {
        var id = WorkItemSeed.DeterministicId(
            ReAccreditationType.Id,
            ReAccreditationSeeder.OrsInterimAuthoritySeedKey
        );
        var item = await persistence.GetByIdAsync(id, cancellationToken);

        if (item is null || !Renumber(item.Payload))
        {
            logger.LogInformation(
                "Migration '{Name}' complete: fixture already current or absent.",
                Name
            );
            return;
        }

        item.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = "interim-site-numbers-renumbered",
                ActionDisplayName = "Interim site numbers renumbered",
                CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                CreatedBy = "migration",
                CreatedByName = "Migration",
                Details = [],
            }
        );

        try
        {
            await persistence.ReplaceAsync(item, cancellationToken);
            logger.LogInformation("Migration '{Name}' complete: fixture renumbered.", Name);
        }
        catch (WorkItemConcurrencyException)
        {
            logger.LogDebug(
                "Concurrency conflict on work item {Id}; skipping — another instance already migrated it.",
                item.Id
            );
        }
    }

    private static bool Renumber(BsonDocument payload)
    {
        if (
            !payload.TryGetValue("overseasSites", out var overseasSites)
            || !overseasSites.IsBsonDocument
            || !overseasSites.AsBsonDocument.TryGetValue("sites", out var sites)
            || !sites.IsBsonArray
        )
        {
            return false;
        }

        var changed = false;

        foreach (
            var site in sites.AsBsonArray.Where(s => s.IsBsonDocument).Select(s => s.AsBsonDocument)
        )
        {
            if (site.TryGetValue("interimSite", out var mirror) && mirror.IsBsonDocument)
                changed |= RenumberOne(mirror.AsBsonDocument);

            if (site.TryGetValue("interimSites", out var list) && list.IsBsonArray)
            {
                foreach (var interim in list.AsBsonArray.Where(i => i.IsBsonDocument))
                    changed |= RenumberOne(interim.AsBsonDocument);
            }
        }

        return changed;
    }

    private static bool RenumberOne(BsonDocument interim)
    {
        if (
            !interim.TryGetValue("siteNumber", out var number)
            || !number.IsString
            || !s_renumbering.TryGetValue(number.AsString, out var renumbered)
        )
        {
            return false;
        }

        interim["siteNumber"] = renumbered;
        return true;
    }
}
