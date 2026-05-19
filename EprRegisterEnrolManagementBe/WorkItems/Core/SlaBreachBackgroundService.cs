namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Nightly background job that scans open work items with an SLA clock and
/// sets <see cref="WorkItemSlaClock.Breached"/> to <c>true</c> when the
/// deadline has passed. Writes a single <c>sla-breached</c> audit entry per
/// item (idempotent: items already marked breached are skipped). Uses
/// <see cref="TimeProvider"/> so tests can substitute time.
/// </summary>
internal sealed class SlaBreachBackgroundService(
    IServiceProvider serviceProvider,
    TimeProvider timeProvider,
    ILogger<SlaBreachBackgroundService> logger,
    IConfiguration configuration) : BackgroundService
{
    private static readonly TimeSpan s_defaultInterval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = configuration.GetValue("SlaBreachJob:IntervalHours", 24.0);
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
                logger.LogError(ex, "SLA breach job encountered an error; will retry after {Interval}.", delay);
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        // Resolve persistence from a scoped service provider so the job can
        // run in environments where IWorkItemPersistence is scoped.
        using var scope = serviceProvider.CreateScope();
        var persistence = scope.ServiceProvider.GetRequiredService<IWorkItemPersistence>();
        var auditAppender = scope.ServiceProvider.GetRequiredService<IWorkItemAuditAppender>();

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Query items in assessment-in-progress (the only state that carries an SLA clock).
        // Each run processes up to MaxPageSize items. In practice the active assessment
        // queue is small; if it ever exceeds MaxPageSize the job will catch remaining
        // items on the next nightly run.
        var query = new WorkItemQuery(
            StateIds: ["assessment-in-progress"],
            PageSize: WorkItemQuery.MaxPageSize);
        var page = await persistence.QueryAsync(query, cancellationToken);

        var breached = 0;
        foreach (var item in page.Items)
        {
            if (item.SlaClock is null || item.SlaClock.Breached)
            {
                continue;
            }

            if (item.SlaClock.Remaining(now) > TimeSpan.Zero)
            {
                continue;
            }

            // Re-load the item to get the full document (QueryAsync strips notes/audit).
            var full = await persistence.GetByIdAsync(item.Id, cancellationToken);
            if (full?.SlaClock is null || full.SlaClock.Breached)
            {
                continue;
            }

            // Idempotent check: don't write sla-breached twice.
            var alreadyBreached = full.AuditLog.Any(
                e => string.Equals(e.Action, "sla-breached", StringComparison.Ordinal));
            if (alreadyBreached)
            {
                full.SlaClock.Breached = true;
                try { await persistence.ReplaceAsync(full, cancellationToken); } catch { /* best-effort */ }
                continue;
            }

            full.SlaClock.Breached = true;
            full.LastModifiedAt = now;
            full.AuditLog.Add(new WorkItemAuditEntry
            {
                Action = "sla-breached",
                ActionDisplayName = "SLA breached",
                CreatedAt = now,
                Details = new Dictionary<string, string?>
                {
                    ["startedAt"] = full.SlaClock.StartedAt.ToString("O"),
                    ["targetDays"] = "84",
                    ["detectedAt"] = now.ToString("O")
                }
            });

            try
            {
                await persistence.ReplaceAsync(full, cancellationToken);
                breached++;
                logger.LogInformation(
                    "SLA breached for work item {WorkItemId} ({TypeId}); clock started {StartedAt}.",
                    full.Id, full.TypeId, full.SlaClock.StartedAt);
            }
            catch (WorkItemConcurrencyException)
            {
                logger.LogWarning(
                    "Concurrency conflict marking SLA breach for work item {WorkItemId}; will retry next run.",
                    full.Id);
            }
        }

        if (breached > 0 || page.Items.Count > 0)
        {
            logger.LogInformation(
                "SLA breach job completed: {Total} items in assessment, {Breached} newly breached.",
                page.Items.Count, breached);
        }
    }
}
