using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Back-fills the single-string <c>payload.material</c> field for
/// re-accreditation work items submitted before the switch away from the
/// legacy <c>payload.materialsHandled</c> array. Older documents only carry
/// <c>materialsHandled: string[]</c>; the frontend's work-items table and
/// "Application details" page now read <c>payload.material</c> exclusively,
/// so without this backfill those pre-existing submissions show a blank
/// material.
///
/// Copies the first entry of <c>materialsHandled</c> into <c>material</c>.
/// <c>materialsHandled</c> itself is left in place — it is harmless dead
/// weight now that nothing reads it.
///
/// Idempotent: items that already have a non-blank <c>payload.material</c>
/// are skipped, as are items with no usable legacy array to copy from.
/// </summary>
internal sealed class ReAccreditationMaterialBackfillMigration(
    ILogger<ReAccreditationMaterialBackfillMigration> logger,
    TimeProvider? timeProvider = null)
    : ReAccreditationMigrationBase(logger)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public override string Name => "ReAccreditation: backfill payload.material from legacy materialsHandled array";

    protected override bool TryMigrate(WorkItem full)
    {
        if (!NeedsBackfill(full.Payload, out var material))
        {
            return false;
        }

        full.Payload["material"] = material;
        full.AuditLog.Add(new WorkItemAuditEntry
        {
            Action = "material-backfilled",
            ActionDisplayName = "Material backfilled",
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            CreatedBy = "migration",
            CreatedByName = "Migration",
            Details = new Dictionary<string, string?>
            {
                ["material"] = material
            }
        });

        return true;
    }

    protected override void LogCompletion(int migrated, int skipped)
    {
        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation(
                "Migration '{Name}' complete: {Backfilled} materials backfilled, {Skipped} already current or lacked legacy data.",
                Name, migrated, skipped);
        }
    }

    private static bool NeedsBackfill(BsonDocument payload, out string material)
    {
        material = "";

        if (payload.TryGetValue("material", out var existing) &&
            !existing.IsBsonNull &&
            !string.IsNullOrWhiteSpace(existing.ToString()))
        {
            return false;
        }

        if (!payload.TryGetValue("materialsHandled", out var raw) ||
            raw is not BsonArray array ||
            array.Count == 0 ||
            array[0].IsBsonNull ||
            string.IsNullOrWhiteSpace(array[0].ToString()))
        {
            return false;
        }

        material = array[0].ToString() ?? "";
        return true;
    }
}
