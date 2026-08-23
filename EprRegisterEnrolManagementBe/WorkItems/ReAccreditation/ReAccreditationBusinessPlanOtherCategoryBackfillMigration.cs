using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Back-fills <c>payload.businessPlan.otherPercent</c> /
/// <c>otherDetail</c> (and rebalances <c>businessCollectionsPercent</c>
/// 25→20 / <c>newMarketsPercent</c> 20→15) onto the
/// <see cref="ReAccreditationSeeder.FullPayloadVerificationSeedKey"/> fixture
/// for any environment that seeded before RA-456 added the seventh "other"
/// business plan category to <see cref="ReAccreditationSeeder"/>.
///
/// <see cref="IWorkItemPersistence.CreateIfAbsentAsync"/> inserts by
/// deterministic id and never updates, so an environment that seeded before
/// the RA-456 change to <see cref="ReAccreditationSeeder.Build"/> silently
/// keeps the old six-category <c>businessPlan</c> forever: re-running the
/// seeder does not fix it (the id already exists), and
/// <c>epr-register-enrol-mgmt-tests</c>'s "shows every business plan
/// category with its detail text" assertion — which expects the seventh
/// category's detail text on this exact work item — fails against such an
/// environment.
///
/// Deliberately scoped to this one known deterministic id, mirroring
/// <see cref="ReAccreditationExporterFixtureBackfillMigration"/>: the
/// intended value is known for certain because it is declared right there in
/// <see cref="ReAccreditationSeeder.Build"/>, whereas a general "businessPlan
/// missing otherPercent" predicate over the whole collection would also
/// match real applicant data that never had a seventh category and has no
/// well-defined value to backfill it with.
///
/// Idempotent: skipped once <c>otherPercent</c> is present, regardless of
/// whether the rebalanced <c>businessCollectionsPercent</c>/
/// <c>newMarketsPercent</c> also made it in on the same run — the three
/// fields are only ever written together by this migration, so there is no
/// partially-applied state to reconcile (unlike the exporter-fixture
/// migration's two independently-added fields).
/// </summary>
internal sealed class ReAccreditationBusinessPlanOtherCategoryBackfillMigration(
    ILogger<ReAccreditationBusinessPlanOtherCategoryBackfillMigration> logger,
    TimeProvider? timeProvider = null) : IWorkItemMigration
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private const int OtherPercent = 10;
    private const string OtherDetail =
        "Contribution to sector-wide research and development initiatives";

    public string Name =>
        "ReAccreditation: backfill business plan 'other' category on the full-payload-verification seed fixture (RA-456)";

    public async Task ApplyAsync(IWorkItemPersistence persistence, CancellationToken cancellationToken)
    {
        var id = WorkItemSeed.DeterministicId(
            ReAccreditationType.Id, ReAccreditationSeeder.FullPayloadVerificationSeedKey);
        var item = await persistence.GetByIdAsync(id, cancellationToken);

        if (item?.Payload.TryGetValue("businessPlan", out var businessPlanValue) != true ||
            !businessPlanValue!.IsBsonDocument)
        {
            logger.LogInformation(
                "Migration '{Name}' complete: fixture absent or has no businessPlan, nothing to do.",
                Name);
            return;
        }

        var businessPlan = businessPlanValue.AsBsonDocument;

        if (businessPlan.Contains("otherPercent"))
        {
            logger.LogInformation("Migration '{Name}' complete: already backfilled.", Name);
            return;
        }

        businessPlan["businessCollectionsPercent"] = 20;
        businessPlan["newMarketsPercent"] = 15;
        businessPlan["otherPercent"] = OtherPercent;
        businessPlan["otherDetail"] = OtherDetail;

        item!.AuditLog.Add(new WorkItemAuditEntry
        {
            Action = "business-plan-other-category-backfilled",
            ActionDisplayName = "Business plan 'other' category backfilled",
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            CreatedBy = "migration",
            CreatedByName = "Migration",
            Details = new Dictionary<string, string?>
            {
                ["otherPercent"] = OtherPercent.ToString(),
                ["otherDetail"] = OtherDetail
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
