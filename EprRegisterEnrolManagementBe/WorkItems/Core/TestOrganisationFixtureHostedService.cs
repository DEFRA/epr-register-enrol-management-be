using Microsoft.Extensions.Hosting;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-000: resets every <see cref="ITestOrganisationFixtureSeeder"/> fixture
/// to its pristine document at startup, so the journey suite finds the same
/// fixtures on every boot however earlier runs changed them. It is
/// registered with Development demo seeding or <c>TestOrganisations__Enabled</c>
/// (see <see cref="WorkItemModuleExtensions.AddWorkItemSeeders"/>), neither of
/// which can be on in prod.
///
/// Like <see cref="WorkItemSeederHostedService"/>, it never fails startup:
/// a Mongo outage is logged and the next boot tries again.
/// </summary>
internal sealed class TestOrganisationFixtureHostedService(
    IServiceProvider serviceProvider,
    ILogger<TestOrganisationFixtureHostedService> logger,
    TimeProvider timeProvider
) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seeders = serviceProvider.GetServices<ITestOrganisationFixtureSeeder>().ToList();
        if (seeders.Count == 0)
        {
            return;
        }

        try
        {
            // Resolved inside the try: constructing the Mongo-backed store
            // probes the database (see WorkItemSeederHostedService).
            var store = serviceProvider.GetRequiredService<IWorkItemFixtureStore>();
            var registry = serviceProvider.GetRequiredService<IWorkItemRegistry>();

            var reset = 0;
            foreach (var seeder in seeders)
            {
                var type = registry.Find(seeder.TypeId);
                if (type is null)
                {
                    logger.LogWarning(
                        "Skipping test organisation fixtures for unknown work item type '{TypeId}'.",
                        seeder.TypeId
                    );
                    continue;
                }

                foreach (var document in seeder.Build(type, timeProvider))
                {
                    await store.ResetAsync(document, cancellationToken);
                    reset++;
                }
            }

            logger.LogInformation("Reset {FixtureCount} test organisation fixture(s).", reset);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Resetting test organisation fixtures failed; continuing startup. Will retry on next boot."
            );
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
