using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-603: copies an overseas reprocessing site's singular <c>interimSite</c> into the new
/// <c>interimSites</c> list.
///
/// <para>
/// An ORS used to hold at most one interim site - the operator journey returned 409 on the second -
/// so every work item written before RA-603 carries a single nested object. It can now hold many,
/// and the regulator's view reads the list. Without this backfill that view would have to
/// understand both shapes indefinitely, and a work item submitted last month would render
/// differently from one submitted today.
/// </para>
///
/// <para>
/// The singular field is deliberately left in place. It is the mirror that consumers which have
/// not moved over still read (see <c>recycling-operations</c> in management-fe), and retiring it
/// is separate work with its own ticket.
/// </para>
///
/// <para>
/// Two things this deliberately does not do. It does not re-derive <c>isNewSite</c>: that flag
/// drives the regulator's "new" badge and a migration is not the place to change what a caseworker
/// is being told. And it does not invent a <c>createdAt</c>: that field exists for auditability,
/// these records genuinely predate it, and a plausible-looking timestamp nobody can vouch for is
/// worse than an absent one.
/// </para>
///
/// <para>
/// Idempotent, and it will not overwrite a list that is already populated - the list is the
/// authority and the mirror may be stale, never the other way round. An ORS that never had an
/// interim site gets an empty list, so consumers read one shape instead of branching on whether
/// the field exists.
/// </para>
///
/// <para>
/// No outbound HTTP: this only rewrites documents, so it is not exposed to the
/// <c>HeaderPropagationValues.Headers</c> failure that has twice caught boot-time work in this
/// service making its first outbound call outside a live request.
/// </para>
/// </summary>
internal sealed class ReAccreditationInterimSitesBackfillMigration(
    ILogger<ReAccreditationInterimSitesBackfillMigration> logger,
    TimeProvider? timeProvider = null
) : ReAccreditationMigrationBase(logger)
{
    public const string AuditAction = "interim-sites-backfilled";
    public const string AuditActionDisplayName = "Interim sites backfilled";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public override string Name =>
        "ReAccreditation: copy singular interimSite into the interimSites list (RA-603)";

    protected override bool TryMigrate(WorkItem full)
    {
        ArgumentNullException.ThrowIfNull(full);

        if (!TryGetOverseasSites(full.Payload, out var sites))
        {
            return false;
        }

        var changed = 0;
        foreach (var site in sites.OfType<BsonDocument>())
        {
            if (Backfill(site))
            {
                changed++;
            }
        }

        if (changed == 0)
        {
            return false;
        }

        full.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = AuditAction,
                ActionDisplayName = AuditActionDisplayName,
                CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                CreatedBy = "migration",
                CreatedByName = "Migration",
                Details = new Dictionary<string, string?>
                {
                    ["overseasSitesChanged"] = changed.ToString(),
                },
            }
        );

        return true;
    }

    protected override void LogCompletion(int migrated, int skipped) =>
        Logger.LogInformation(
            "Migration '{Name}' complete: {Migrated} work items backfilled, {Skipped} already current or without overseas sites.",
            Name,
            migrated,
            skipped
        );

    private static bool TryGetOverseasSites(BsonDocument payload, out BsonArray sites)
    {
        sites = [];

        if (
            !payload.TryGetValue("overseasSites", out var section)
            || section is not BsonDocument sectionDoc
            || !sectionDoc.TryGetValue("sites", out var raw)
            || raw is not BsonArray array
        )
        {
            return false;
        }

        sites = array;
        return sites.Count > 0;
    }

    /// <returns><c>true</c> when this site's document was changed.</returns>
    private static bool Backfill(BsonDocument site)
    {
        // Already in the new shape: leave it entirely alone. The mirror can lag the list, so
        // rebuilding the list from it would be a downgrade rather than a migration.
        if (
            site.TryGetValue("interimSites", out var existing)
            && existing is BsonArray { Count: > 0 }
        )
        {
            return false;
        }

        var hasSingular =
            site.TryGetValue("interimSite", out var singular) && singular is BsonDocument;

        // An empty list is still worth writing once, so every site reads the same way.
        if (!hasSingular)
        {
            if (existing is BsonArray)
            {
                return false;
            }

            site["interimSites"] = new BsonArray();
            return true;
        }

        site["interimSites"] = new BsonArray { singular };
        return true;
    }
}
