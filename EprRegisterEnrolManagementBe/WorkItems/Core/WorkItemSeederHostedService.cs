using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Runs every registered <see cref="IWorkItemSeeder"/> once at startup.
/// Each seeder yields items with a deterministic <see cref="WorkItem.Id"/>
/// (see <see cref="WorkItemSeed.DeterministicId"/>) so insertion is
/// idempotent: re-running on a populated database is a no-op, and two
/// instances racing during a multi-instance dev rollout still produce
/// exactly one document per seed entry — the first writer wins, every
/// loser surfaces as a duplicate-key swallow inside
/// <see cref="IWorkItemPersistence.CreateIfAbsentAsync"/>. (epr-33c —
/// the previous "TotalCount==0 then insert N items" check was non-atomic
/// and let two instances both seed, producing duplicates with fresh
/// GUIDs.)
///
/// Seeding is opt-in via the <c>WorkItems:SeedOnStartup</c> configuration
/// value (defaults to <c>false</c>). Tests therefore never trigger a Mongo
/// connection during host startup, which would otherwise stall every
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>-
/// based test by the driver's connect timeout.
///
/// The service also does not throw if persistence is unreachable —
/// startup must succeed even when MongoDB is briefly unavailable; the next
/// restart will re-attempt the seed if the items are still missing.
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

            var insertedTotal = 0;
            var skippedTotal = 0;
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
                    var inserted = await persistence.CreateIfAbsentAsync(item, cancellationToken);
                    if (inserted)
                    {
                        insertedTotal++;
                    }
                    else
                    {
                        skippedTotal++;
                    }
                }
            }

            logger.LogInformation(
                "Seeded {Inserted} new work item(s) across {SeederCount} module seeder(s); {Skipped} already present.",
                insertedTotal, seeders.Count, skippedTotal);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Work item seeding failed; continuing startup. Will retry on next boot if items are still missing.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}