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

        // IncludeArchived=true so the approved-exclusion filter does not hide
        // the items this job specifically needs to process.
        var query = new WorkItemQuery(
            StateIds: ["approved"],
            IncludeArchived: true,
            PageSize: WorkItemQuery.MaxPageSize);

        var page = await persistence.QueryAsync(query, cancellationToken);

        var stamped = 0;
        foreach (var item in page.Items)
        {
            // Skip items that haven't been in the approved state long enough.
            // Spec requires ">7 days", so items at exactly the threshold are not yet eligible.
            if (item.LastModifiedAt >= archiveThreshold)
            {
                continue;
            }

            // Skip items that are already stamped (idempotent).
            if (item.Payload.Contains(ArchivedAtPayloadKey))
            {
                continue;
            }

            // Re-load the full document (QueryAsync strips notes/audit).
            var full = await persistence.GetByIdAsync(item.Id, cancellationToken);
            if (full is null || full.Payload.Contains(ArchivedAtPayloadKey))
            {
                continue;
            }

            var approvedAt = item.LastModifiedAt;

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

        if (stamped > 0 || page.Items.Count > 0)
        {
            logger.LogInformation(
                "Archive job completed: {Total} approved items scanned, {Stamped} newly archived.",
                page.Items.Count, stamped);
        }
    }
}
