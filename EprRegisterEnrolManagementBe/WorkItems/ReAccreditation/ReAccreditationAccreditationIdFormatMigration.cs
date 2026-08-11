using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// epr-accreditation-id-format AC02: regenerates <c>payload.accreditationId</c>
/// for every approved work item still carrying an id in the old
/// <c>ACC-{Year}-{Material}-{ULID8}</c> shape, replacing it with the new
/// fixed-width, 16-character <c>A{Year:2}{Agency}{OperatorType}{OrgId:6}{PostcodeSuffix:3}{Material:2}</c>
/// format <see cref="AccreditationIdGenerator"/> now issues at approval
/// time.
///
/// <para>
/// Gated like <see cref="ReAccreditationIsNewSiteCorrectionMigration"/>: an
/// already-issued accreditation id may already have been quoted to an
/// operator or regulator, so silently rewriting it on every deploy would be
/// a worse failure mode than leaving old-format ids in place. Off by
/// default (<see cref="EnabledConfigKey"/>) and a dry run unless
/// <see cref="ApplyConfigKey"/> is explicitly set, so the affected
/// population and the exact before/after values can be reviewed before
/// anything is written.
/// </para>
///
/// <para>
/// Idempotent: an item whose <c>accreditationId</c> is already
/// <see cref="NewFormatLength"/> characters long (the new format's fixed
/// width) is skipped without a fresh lookup, so re-running after a partial
/// apply only touches what is left.
/// </para>
///
/// <para>
/// Takes <see cref="IServiceProvider"/> rather than
/// <see cref="IAccreditationIdGenerator"/> directly and resolves it lazily
/// inside <see cref="ApplyAsync"/>, after the <see cref="EnabledConfigKey"/>
/// check: <see cref="WorkItemMigrationHostedService"/> constructs every
/// registered <see cref="IWorkItemMigration"/> up front to run them, and a
/// constructor-injected generator would pull in
/// <see cref="AccreditationIdLookup"/>'s live Mongo connection at every host
/// startup regardless of whether the backfill is enabled.
/// </para>
/// </summary>
internal sealed class ReAccreditationAccreditationIdFormatMigration(
    IConfiguration configuration,
    IServiceProvider serviceProvider,
    ILogger<ReAccreditationAccreditationIdFormatMigration> logger,
    TimeProvider? timeProvider = null) : IWorkItemMigration
{
    public const string EnabledConfigKey = "Diagnostics:AccreditationIdFormatBackfillEnabled";
    public const string ApplyConfigKey = "Diagnostics:AccreditationIdFormatBackfillApply";

    public const string AuditAction = "accreditation-id-format-backfilled";
    public const string AuditActionDisplayName = "Accreditation id format backfilled";

    private const int NewFormatLength = 16;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string Name =>
        "ReAccreditation: backfill payload.accreditationId to the new fixed-width format " +
        "(epr-accreditation-id-format)";

    public async Task ApplyAsync(IWorkItemPersistence persistence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(persistence);

        if (!configuration.GetValue(EnabledConfigKey, false))
        {
            return;
        }

        var generator = serviceProvider.GetRequiredService<IAccreditationIdGenerator>();
        var apply = configuration.GetValue(ApplyConfigKey, false);
        var backfilled = 0;
        var skipped = 0;
        var page = 1;
        const int pageSize = WorkItemQuery.MaxPageSize;

        logger.LogInformation(
            "epr-accreditation-id-format backfill starting. Mode={Mode}.",
            apply ? "APPLY" : "DRY RUN (nothing will be written)");

        while (true)
        {
            var result = await persistence.QueryAsync(
                new WorkItemQuery(
                    TypeIds: [ReAccreditationType.Id],
                    StateIds: ["approved"],
                    Page: page,
                    PageSize: pageSize,
                    IncludeArchived: true),
                cancellationToken);

            foreach (var candidate in result.Items)
            {
                // QueryAsync excludes Notes/AuditLog — fetch the full document
                // before mutating so a subsequent replace doesn't wipe them.
                var full = await persistence.GetByIdAsync(candidate.Id, cancellationToken);
                if (full is null || !NeedsBackfill(full.Payload, out var existingId, out var year))
                {
                    skipped++;
                    continue;
                }

                var newId = await generator.GenerateAsync(full.Payload, year, cancellationToken);

                if (!apply)
                {
                    logger.LogInformation(
                        "epr-accreditation-id-format DRY RUN would replace work item {WorkItemId} " +
                        "accreditationId '{OldId}' with '{NewId}'.",
                        full.Id, existingId, newId);
                    backfilled++;
                    continue;
                }

                full.Payload["accreditationId"] = newId;
                full.AuditLog.Add(new WorkItemAuditEntry
                {
                    Action = AuditAction,
                    ActionDisplayName = AuditActionDisplayName,
                    CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                    CreatedBy = "migration",
                    CreatedByName = "Migration",
                    Details = new Dictionary<string, string?>
                    {
                        ["previousAccreditationId"] = existingId,
                        ["accreditationId"] = newId
                    }
                });

                try
                {
                    await persistence.ReplaceAsync(full, cancellationToken);
                    backfilled++;
                }
                catch (WorkItemConcurrencyException)
                {
                    logger.LogDebug(
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

        logger.LogInformation(
            "epr-accreditation-id-format backfill complete. Mode={Mode}. Items {ItemsVerb}: " +
            "{Backfilled}. Skipped (already current or unreadable): {Skipped}.",
            apply ? "APPLY" : "DRY RUN",
            apply ? "backfilled" : "that would be backfilled",
            backfilled,
            skipped);
    }

    private static bool NeedsBackfill(BsonDocument payload, out string? existingId, out int year)
    {
        existingId = null;
        year = 0;

        if (!payload.TryGetValue("accreditationId", out var idValue) || idValue.IsBsonNull)
        {
            return false;
        }

        existingId = idValue.ToString();
        if (string.IsNullOrWhiteSpace(existingId) || existingId!.Length == NewFormatLength)
        {
            // Already the new fixed-width shape (or blank, which the
            // approval service would never have produced) — nothing to do.
            return false;
        }

        year = payload.TryGetValue("accreditationYear", out var yearValue) && yearValue.IsNumeric
            ? yearValue.ToInt32()
            : DateTime.UtcNow.Year;

        return true;
    }
}
