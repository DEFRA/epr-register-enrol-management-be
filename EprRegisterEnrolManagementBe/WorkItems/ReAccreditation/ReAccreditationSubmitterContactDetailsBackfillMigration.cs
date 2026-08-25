using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Back-fills <c>payload.submitterContactDetails</c> onto the
/// <see cref="ReAccreditationSeeder.AdditionalInformationExporterSeedKey"/>
/// fixture for any environment that seeded before RA-480 added the field to
/// <see cref="ReAccreditationSeeder"/>.
///
/// <see cref="IWorkItemPersistence.CreateIfAbsentAsync"/> inserts by
/// deterministic id and never updates, so an environment that seeded before
/// the RA-480 change to <see cref="ReAccreditationSeeder.Build"/> silently
/// keeps the old payload with no <c>submitterContactDetails</c> forever:
/// re-running the seeder does not fix it (the id already exists), and the
/// companion <c>epr-register-enrol-mgmt-tests</c> assertions that expect the
/// populated contact rows on this exact fixture fail against such an
/// environment.
///
/// Deliberately scoped to this one known deterministic id, mirroring
/// <see cref="ReAccreditationBusinessPlanOtherCategoryBackfillMigration"/>:
/// the intended value is known for certain because it is declared right
/// there in <see cref="ReAccreditationSeeder.Build"/>, whereas a general
/// "no submitterContactDetails" predicate over the whole collection would
/// also match every other real and seeded work item, which has never carried
/// this field and has no well-defined value to backfill it with.
///
/// Idempotent: skipped once <c>submitterContactDetails</c> is present.
/// </summary>
internal sealed class ReAccreditationSubmitterContactDetailsBackfillMigration(
    ILogger<ReAccreditationSubmitterContactDetailsBackfillMigration> logger,
    TimeProvider? timeProvider = null) : IWorkItemMigration
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private const string FullName = "Barton Deckow";
    private const string Email = "REEXServiceTeam@defra.gov.uk";
    private const string Phone = "0111 478 4919";
    private const string JobTitle = "Human Infrastructure Architect";

    public string Name =>
        "ReAccreditation: backfill submitterContactDetails on the additional-information-exporter seed fixture (RA-480)";

    public async Task ApplyAsync(IWorkItemPersistence persistence, CancellationToken cancellationToken)
    {
        var id = WorkItemSeed.DeterministicId(
            ReAccreditationType.Id, ReAccreditationSeeder.AdditionalInformationExporterSeedKey);
        var item = await persistence.GetByIdAsync(id, cancellationToken);

        if (item is null)
        {
            logger.LogInformation("Migration '{Name}' complete: fixture absent, nothing to do.", Name);
            return;
        }

        if (item.Payload.Contains("submitterContactDetails"))
        {
            logger.LogInformation("Migration '{Name}' complete: already backfilled.", Name);
            return;
        }

        item.Payload["submitterContactDetails"] = new BsonDocument
        {
            ["fullName"] = FullName,
            ["email"] = Email,
            ["phone"] = Phone,
            ["jobTitle"] = JobTitle,
        };

        item.AuditLog.Add(new WorkItemAuditEntry
        {
            Action = "submitter-contact-details-backfilled",
            ActionDisplayName = "Submitter contact details backfilled",
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            CreatedBy = "migration",
            CreatedByName = "Migration",
            Details = new Dictionary<string, string?>
            {
                ["fullName"] = FullName,
                ["email"] = Email,
                ["phone"] = Phone,
                ["jobTitle"] = JobTitle
            }
        });

        try
        {
            await persistence.ReplaceAsync(item, cancellationToken);
            logger.LogInformation("Migration '{Name}' complete: fixture backfilled.", Name);
        }
        catch (WorkItemConcurrencyException)
        {
            logger.LogDebug(
                "Concurrency conflict on work item {Id}; skipping — another instance already migrated it.",
                item.Id);
        }
    }
}
