using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Nightly background job that stamps <c>payload.archivedAt</c> (UTC ISO-8601)
/// on every work item that has been in a terminal state
/// (approved/rejected/withdrawn) for more than <see cref="ArchiveAfterDays"/>
/// days. The stamp is a presentation aid — the item stays in its terminal
/// state and the <c>GET /work-items</c> list already hides terminal-state items
/// by default (<c>includeArchived=false</c>). The UI surfaces the date so
/// regulators know when archiving occurred.
/// Uses <see cref="TimeProvider"/> so tests can substitute time.
/// </summary>
internal sealed class ArchiveBackgroundService(
    IServiceProvider serviceProvider,
    TimeProvider timeProvider,
    ILogger<ArchiveBackgroundService> logger,
    IConfiguration configuration,
    IWorkItemRegistry registry) : BackgroundService
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

        var terminalStateIds = TerminalStates.Ids(registry).ToList();

        // No terminal states means nothing is archivable. Bail out explicitly:
        // querying with an empty StateIds set would otherwise match every item
        // rather than none, so we must not fall through to the scan loop.
        if (terminalStateIds.Count == 0)
        {
            return;
        }

        var stamped = 0;
        var totalScanned = 0;
        var pageNumber = 1;

        while (true)
        {
            // IncludeArchived=true so the terminal-state exclusion filter does
            // not hide the items this job specifically needs to process.
            var query = new WorkItemQuery(
                StateIds: terminalStateIds,
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

                // Derive the time the item entered its current terminal state
                // from the audit log so that post-decision writes
                // (assignments, SLA stamps) that bump LastModifiedAt do not reset
                // the 7-day clock. We match the LAST audit entry whose toStateId
                // equals the item's current terminal state — approve (which
                // bypasses the generic engine) and reject/withdraw (which go
                // through it) all write an action-applied entry with that key.
                // Falls back to LastModifiedAt for items that pre-date audit entries.
                var enteredTerminalStateAt = full.AuditLog
                    .LastOrDefault(e =>
                        e.Action == "action-applied" &&
                        e.Details.TryGetValue("toStateId", out var to) &&
                        string.Equals(to, full.StateId, StringComparison.OrdinalIgnoreCase))
                    ?.CreatedAt ?? full.LastModifiedAt;

                // Skip items that haven't been in the terminal state long enough.
                // Spec requires ">7 days", so items at exactly the threshold are not yet eligible.
                if (enteredTerminalStateAt >= archiveThreshold)
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
                        ["enteredStateAt"] = enteredTerminalStateAt.ToString("O"),
                        ["archivedAt"] = now.ToString("O")
                    }
                });

                try
                {
                    await persistence.ReplaceAsync(full, cancellationToken);
                    stamped++;
                    logger.LogInformation(
                        "Archived work item {WorkItemId} ({TypeId}); entered terminal state {StateId} at {EnteredStateAt}.",
                        full.Id, full.TypeId, full.StateId, enteredTerminalStateAt);
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
                "Archive job completed: {Total} terminal-state items scanned, {Stamped} newly archived.",
                totalScanned, stamped);
        }
    }
}
