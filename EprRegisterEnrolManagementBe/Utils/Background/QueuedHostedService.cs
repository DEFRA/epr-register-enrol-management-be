namespace EprRegisterEnrolManagementBe.Utils.Background;

/// <summary>
/// RA-132 hosted service that drains <see cref="IBackgroundTaskQueue"/>.
/// Each job is dispatched inside a fresh DI scope so scoped services
/// (Mongo persistence, audit appender) are not shared with the
/// originating HTTP request scope. Exceptions are logged and swallowed
/// — a single bad job must never take the worker down.
/// </summary>
internal sealed class QueuedHostedService(
    IBackgroundTaskQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<QueuedHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Background task queue worker starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            Func<IServiceProvider, CancellationToken, Task> workItem;
            try
            {
                workItem = await queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                using var scope = scopeFactory.CreateScope();
                await workItem(scope.ServiceProvider, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                                       || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Queued background work item threw an unhandled exception.");
            }
        }

        logger.LogInformation("Background task queue worker stopping.");
    }
}
