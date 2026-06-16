using Microsoft.Extensions.Hosting;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Discovers every <see cref="IWorkItemMigration"/> registered with DI and
/// runs them in sequence at application startup. Mirrors the
/// <see cref="WorkItemSeederHostedService"/> fault-tolerance posture: a
/// failed migration is logged as a warning and startup continues — the
/// migration will be re-attempted on the next boot.
///
/// Unlike the seeder, migrations run in all environments (including Production)
/// because they are needed to keep existing documents consistent with evolving
/// schema expectations.
/// </summary>
internal sealed class WorkItemMigrationHostedService(
    IServiceProvider serviceProvider,
    ILogger<WorkItemMigrationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var migrations = serviceProvider.GetServices<IWorkItemMigration>().ToList();
        if (migrations.Count == 0)
        {
            return;
        }

        IWorkItemPersistence? persistence;
        try
        {
            persistence = serviceProvider.GetService<IWorkItemPersistence>();
            if (persistence is null)
            {
                logger.LogDebug(
                    "Work item persistence not available; skipping {Count} migration(s).",
                    migrations.Count);
                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to resolve persistence; skipping {Count} work item migration(s). Will retry on next boot.",
                migrations.Count);
            return;
        }

        foreach (var migration in migrations)
        {
            try
            {
                logger.LogInformation(
                    "Applying work item migration: {MigrationName}", migration.Name);
                await migration.ApplyAsync(persistence, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Work item migration '{MigrationName}' failed; continuing startup. Will retry on next boot.",
                    migration.Name);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
