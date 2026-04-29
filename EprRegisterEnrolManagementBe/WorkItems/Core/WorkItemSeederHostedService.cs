using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Runs every registered <see cref="IWorkItemSeeder"/> once at startup, but
/// only when the persisted work item collection is empty. This makes seed
/// data safe to leave enabled in any environment that opts in: a fresh
/// <c>docker compose up</c> gets a populated UI, while an existing database
/// is never touched.
///
/// Seeding is opt-in via the <c>WorkItems:SeedOnStartup</c> configuration
/// value (defaults to <c>false</c>). Tests therefore never trigger a Mongo
/// connection during host startup, which would otherwise stall every
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>-
/// based test by the driver's connect timeout.
///
/// The service also does not throw if persistence is unreachable —
/// startup must succeed even when MongoDB is briefly unavailable; the next
/// restart will re-attempt the seed if the collection is still empty.
/// </summary>
internal sealed class WorkItemSeederHostedService(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<WorkItemSeederHostedService> logger,
    TimeProvider? timeProvider = null) : IHostedService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("WorkItems:SeedOnStartup", false))
        {
            return;
        }

        var seeders = serviceProvider.GetServices<IWorkItemSeeder>().ToList();
        if (seeders.Count == 0)
        {
            return;
        }

        try
        {
            // Resolving IWorkItemPersistence constructs the Mongo-backed
            // service, which probes the database during index creation.
            // That probe can throw when Mongo is unavailable (e.g. inside
            // a Docker build running integration tests), so it must sit
            // inside the try/catch alongside the rest of the seed work —
            // startup must never fail because the seeder couldn't run.
            var persistence = serviceProvider.GetService<IWorkItemPersistence>();
            var registry = serviceProvider.GetService<IWorkItemRegistry>();
            if (persistence is null || registry is null)
            {
                logger.LogDebug(
                    "Work item persistence or registry not available; skipping seed.");
                return;
            }

            var existing = await persistence.QueryAsync(
                new WorkItemQuery { Page = 1, PageSize = 1 },
                cancellationToken);

            if (existing.TotalCount > 0)
            {
                logger.LogInformation(
                    "Work item collection already contains {Count} items; skipping seed.",
                    existing.TotalCount);
                return;
            }

            var seededTotal = 0;
            foreach (var seeder in seeders)
            {
                var type = registry.Find(seeder.TypeId);
                if (type is null)
                {
                    logger.LogWarning(
                        "Skipping seeder for unknown work item type '{TypeId}'.",
                        seeder.TypeId);
                    continue;
                }

                var snapshot = WorkItemTemplateSnapshot.Capture(type);
                foreach (var item in seeder.Build(type, _timeProvider))
                {
                    item.TemplateSnapshot ??= snapshot;
                    item.TemplateVersion ??= snapshot.TemplateVersion;
                    await persistence.CreateAsync(item, cancellationToken);
                    seededTotal++;
                }
            }

            logger.LogInformation(
                "Seeded {Count} work item(s) across {SeederCount} module seeder(s).",
                seededTotal, seeders.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Work item seeding failed; continuing startup. Will retry on next boot if collection is still empty.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
