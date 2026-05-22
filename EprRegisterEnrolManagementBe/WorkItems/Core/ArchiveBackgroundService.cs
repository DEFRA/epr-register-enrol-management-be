using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Nightly background job that stamps <c>payload.archivedAt</c> (UTC ISO-8601)
/// on every work item that has been in the <c>approved</c> state for more than
/// <see cref="ArchiveAfterDays"/> days. The stamp is a presentation aid — the
/// item stays in the <c>approved</c> state and the <c>GET /work-items</c> list
/// already hides approved items by default (<c>includeArchived=false</c>). The
/// UI surface the date so regulators know when archiving occurred.
/// Uses <see cref="TimeProvider"/> so tests can substitute time.
/// </summary>
internal sealed class ArchiveBackgroundService(
    IServiceProvider serviceProvider,
    TimeProvider timeProvider,
    ILogger<ArchiveBackgroundService> logger,
    IConfiguration configuration) : BackgroundService
{
    internal const int ArchiveAfterDays = 7;
    internal const string ArchivedAtPayloadKey = "archivedAt";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = configuration.GetValue("ArchiveJob:IntervalHours", 24.0);
        var delay = TimeSpan.FromHours(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Archive job encountered an error; will retry after {Interval}.", delay);
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var persistence = scope.ServiceProvider.GetRequiredService<IWorkItemPersistence>();

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var archiveThreshold = now - TimeSpan.FromDays(ArchiveAfterDays);

        var batchSize = Math.Clamp(
            configuration.GetValue("ArchiveJob:BatchSize", WorkItemQuery.MaxPageSize),
            WorkItemQuery.MinPageSize,
            WorkItemQuery.MaxPageSize);

        var stamped = 0;
        var totalScanned = 0;
        var pageNumber = 1;

        while (true)
        {
            // IncludeArchived=true so the approved-exclusion filter does not hide
            // the items this job specifically needs to process.
            var query = new WorkItemQuery(
                StateIds: ["approved"],
                IncludeArchived: true,
                Page: pageNumber,
                PageSize: batchSize);

            var page = await persistence.QueryAsync(query, cancellationToken);
            totalScanned += page.Items.Count;

            foreach (var item in page.Items)
            {
                // Skip items that are already stamped (idempotent).
                if (item.Payload.Contains(ArchivedAtPayloadKey))
                    continue;

                // Re-load the full document (QueryAsync strips notes/audit).
                var full = await persistence.GetByIdAsync(item.Id, cancellationToken);
                if (full is null || full.Payload.Contains(ArchivedAtPayloadKey))
                    continue;

                // Derive the approval timestamp from the audit log so that
                // post-approval writes (notes, assignments, SLA stamps) that bump
                // LastModifiedAt do not reset the 7-day clock. Falls back to
                // LastModifiedAt for items that pre-date audit entries.
                var approvedAt = full.AuditLog
                    .LastOrDefault(e =>
                        e.Action == "action-applied" &&
                        e.Details.TryGetValue("toStateId", out var to) &&
                        to == "approved")
                    ?.CreatedAt ?? full.LastModifiedAt;

                // Skip items that haven't been in the approved state long enough.
                // Spec requires ">7 days", so items at exactly the threshold are not yet eligible.
                if (approvedAt >= archiveThreshold)
                    continue;

                full.Payload[ArchivedAtPayloadKey] = new BsonDateTime(now);
                full.LastModifiedAt = now;
                full.AuditLog.Add(new WorkItemAuditEntry
                {
                    Action = "archived",
                    ActionDisplayName = "Archived",
                    CreatedAt = now,
                    Details = new Dictionary<string, string?>
                    {
                        ["approvedAt"] = approvedAt.ToString("O"),
                        ["archivedAt"] = now.ToString("O")
                    }
                });

                try
                {
                    await persistence.ReplaceAsync(full, cancellationToken);
                    stamped++;
                    logger.LogInformation(
                        "Archived work item {WorkItemId} ({TypeId}); approved at {ApprovedAt}.",
                        full.Id, full.TypeId, approvedAt);
                }
                catch (WorkItemConcurrencyException)
                {
                    logger.LogWarning(
                        "Concurrency conflict stamping archivedAt on work item {WorkItemId}; will retry next run.",
                        full.Id);
                }
            }

            // A partial page means we've reached the end of the result set.
            if (page.Items.Count < batchSize)
                break;

            pageNumber++;
        }

        if (stamped > 0 || totalScanned > 0)
        {
            logger.LogInformation(
                "Archive job completed: {Total} approved items scanned, {Stamped} newly archived.",
                totalScanned, stamped);
        }
    }
}
