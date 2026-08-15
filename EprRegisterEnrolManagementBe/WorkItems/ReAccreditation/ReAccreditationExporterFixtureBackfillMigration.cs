using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Back-fills <c>payload.wasteProcessingType = "exporter"</c> (and
/// <c>payload.companyRegisterAddressPostcode</c>) onto the two seeded
/// fixtures that carry <c>overseasSites</c>/BES evidence
/// (<c>full-payload-verification</c> and
/// <see cref="ReAccreditationSeeder.OrsInterimAuthoritySeedKey"/>) but predate
/// RA-412's addition of those fields to <see cref="ReAccreditationSeeder"/>.
///
/// <see cref="IWorkItemPersistence.CreateIfAbsentAsync"/> inserts by
/// deterministic id and never updates, so any environment that seeded before
/// the RA-412 change to <see cref="ReAccreditationSeeder.Build"/> silently
/// keeps the old documents: the BES/ORS sections on those two items stop
/// rendering once combined with management-fe's RA-412 change, and
/// re-running the seeder does not fix it (the ids already exist).
///
/// Deliberately scoped to these two known deterministic ids rather than a
/// general "has overseasSites but no wasteProcessingType" predicate over the
/// whole collection: RA-412 explicitly decided that a REAL pre-RA-314
/// submission with no <c>wasteProcessingType</c> must keep falling back to
/// "Reprocessor" — no visual regression for old data — even if it happens to
/// carry overseas site data. A broad predicate would silently reclassify
/// that real data as Exporter, which is the opposite of what the ticket
/// asked for. Only the seeder's own two fixtures — whose intended value is
/// known for certain, because it is declared right there in
/// <see cref="ReAccreditationSeeder.Build"/> — are safe to touch here.
///
/// Idempotent: an id that already carries <c>wasteProcessingType</c> is
/// skipped, as is either id that does not exist (a from-scratch environment
/// that seeded after the RA-412 change already has both fields).
/// </summary>
internal sealed class ReAccreditationExporterFixtureBackfillMigration(
    ILogger<ReAccreditationExporterFixtureBackfillMigration> logger,
    TimeProvider? timeProvider = null) : IWorkItemMigration
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private static readonly (string SeedKey, string CompanyRegisterAddressPostcode)[] s_fixtures =
    [
        ("full-payload-verification", "G2 1AL"),
        (ReAccreditationSeeder.OrsInterimAuthoritySeedKey, "SA1 1AA"),
    ];

    public string Name =>
        "ReAccreditation: backfill wasteProcessingType on the overseas-sites seed fixtures (RA-412)";

    public async Task ApplyAsync(IWorkItemPersistence persistence, CancellationToken cancellationToken)
    {
        var backfilled = 0;
        var skipped = 0;

        foreach (var (seedKey, companyRegisterAddressPostcode) in s_fixtures)
        {
            var id = WorkItemSeed.DeterministicId(ReAccreditationType.Id, seedKey);
            var item = await persistence.GetByIdAsync(id, cancellationToken);

            if (item is null || AlreadyExporter(item.Payload))
            {
                skipped++;
                continue;
            }

            item.Payload["wasteProcessingType"] = "exporter";
            item.Payload["companyRegisterAddressPostcode"] = companyRegisterAddressPostcode;
            item.AuditLog.Add(new WorkItemAuditEntry
            {
                Action = "waste-processing-type-backfilled",
                ActionDisplayName = "Waste processing type backfilled",
                CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                CreatedBy = "migration",
                CreatedByName = "Migration",
                Details = new Dictionary<string, string?>
                {
                    ["wasteProcessingType"] = "exporter"
                }
            });

            try
            {
                await persistence.ReplaceAsync(item, cancellationToken);
                backfilled++;
            }
            catch (WorkItemConcurrencyException)
            {
                logger.LogDebug(
                    "Concurrency conflict on work item {Id}; skipping — another instance already migrated it.",
                    item.Id);
                skipped++;
            }
        }

        logger.LogInformation(
            "Migration '{Name}' complete: {Backfilled} fixtures backfilled, {Skipped} already current or absent.",
            Name, backfilled, skipped);
    }

    private static bool AlreadyExporter(MongoDB.Bson.BsonDocument payload) =>
        payload.TryGetValue("wasteProcessingType", out var value) &&
        value.IsString &&
        string.Equals(value.AsString, "exporter", StringComparison.OrdinalIgnoreCase);
}
