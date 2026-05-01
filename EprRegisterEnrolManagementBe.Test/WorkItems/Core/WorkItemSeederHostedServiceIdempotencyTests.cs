using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// epr-33c: pin the seeder's idempotency contract end-to-end through
/// real MongoDB. Booting the hosted service twice — or two instances
/// concurrently — must converge on exactly one document per seed
/// entry. Backed by Ephemeral MongoDB so the unique <c>_id</c> index
/// (the actual race-arbiter) is in play, not faked away.
/// </summary>
public sealed class WorkItemSeederHostedServiceIdempotencyTests
    : IClassFixture<MongoIntegrationFixture>, IAsyncDisposable
{
    private readonly MongoIntegrationFixture _fixture;
    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _clientFactory;
    private readonly WorkItemPersistence _persistence;
    private readonly IServiceProvider _services;

    static WorkItemSeederHostedServiceIdempotencyTests()
    {
        MongoConventions.Register();
        WorkItemBsonRegistration.Register();
    }

    public WorkItemSeederHostedServiceIdempotencyTests(MongoIntegrationFixture fixture)
    {
        _fixture = fixture;
        _databaseName = $"work_items_seeder_test_{Guid.NewGuid():N}";
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new WorkItemPersistence(_clientFactory, NullLoggerFactory.Instance);

        var registry = new WorkItemRegistry(new IWorkItemType[] { new ReAccreditationType() });

        _services = new ServiceCollection()
            .AddSingleton<IWorkItemPersistence>(_persistence)
            .AddSingleton<IWorkItemRegistry>(registry)
            .AddSingleton<IWorkItemSeeder, ReAccreditationSeeder>()
            .BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);
    }

    [Fact]
    public async Task Running_the_seeder_twice_does_not_duplicate_items()
    {
        var first = BuildHostedService();
        await first.StartAsync(TestContext.Current.CancellationToken);

        var afterFirst = await _persistence.QueryAsync(
            new WorkItemQuery { Page = 1, PageSize = 100 },
            TestContext.Current.CancellationToken);
        var initialCount = afterFirst.TotalCount;
        Assert.True(initialCount > 0, "First run should have seeded at least one item.");

        // Run again — the previous (buggy) implementation would have
        // skipped because the collection was non-empty; the new
        // implementation walks every seed item but each is a no-op
        // because the deterministic id already exists.
        var second = BuildHostedService();
        await second.StartAsync(TestContext.Current.CancellationToken);

        var afterSecond = await _persistence.QueryAsync(
            new WorkItemQuery { Page = 1, PageSize = 100 },
            TestContext.Current.CancellationToken);
        Assert.Equal(initialCount, afterSecond.TotalCount);
    }

    [Fact]
    public async Task Concurrent_seeder_runs_converge_on_a_single_set_of_documents()
    {
        // Simulates the multi-instance dev rollout that originally
        // surfaced epr-33c. Two hosted services start in parallel;
        // both observe an empty collection on the way in and both
        // walk the seeder. The Mongo unique _id index must collapse
        // every duplicate insert.
        var a = BuildHostedService();
        var b = BuildHostedService();

        await Task.WhenAll(
            a.StartAsync(TestContext.Current.CancellationToken),
            b.StartAsync(TestContext.Current.CancellationToken));

        var page = await _persistence.QueryAsync(
            new WorkItemQuery { Page = 1, PageSize = 100 },
            TestContext.Current.CancellationToken);

        // Build a baseline single-instance run in a fresh database
        // and compare the totals: parallel must equal serial.
        var baseline = await BuildBaselineCountAsync();
        Assert.Equal(baseline, page.TotalCount);

        // No id collisions — each id appears exactly once.
        var ids = page.Items.Select(i => i.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    private WorkItemSeederHostedService BuildHostedService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkItems:SeedOnStartup"] = "true"
            })
            .Build();

        return new WorkItemSeederHostedService(
            _services,
            config,
            NullLogger<WorkItemSeederHostedService>.Instance,
            new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero)));
    }

    private async Task<long> BuildBaselineCountAsync()
    {
        var dbName = $"work_items_seeder_baseline_{Guid.NewGuid():N}";
        var clientFactory = new TestMongoDbClientFactory(_fixture.ConnectionString, dbName);
        try
        {
            var persistence = new WorkItemPersistence(clientFactory, NullLoggerFactory.Instance);
            var registry = new WorkItemRegistry(new IWorkItemType[] { new ReAccreditationType() });
            var services = new ServiceCollection()
                .AddSingleton<IWorkItemPersistence>(persistence)
                .AddSingleton<IWorkItemRegistry>(registry)
                .AddSingleton<IWorkItemSeeder, ReAccreditationSeeder>()
                .BuildServiceProvider();
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WorkItems:SeedOnStartup"] = "true"
                })
                .Build();
            var hosted = new WorkItemSeederHostedService(
                services, config, NullLogger<WorkItemSeederHostedService>.Instance,
                new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero)));
            await hosted.StartAsync(TestContext.Current.CancellationToken);
            var page = await persistence.QueryAsync(
                new WorkItemQuery { Page = 1, PageSize = 100 },
                TestContext.Current.CancellationToken);
            return page.TotalCount;
        }
        finally
        {
            await clientFactory.GetClient().DropDatabaseAsync(dbName);
        }
    }
}
