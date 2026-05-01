using EphemeralMongo;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.Test.TestSupport;

/// <summary>
/// xUnit class fixture that boots a single ephemeral <c>mongod</c>
/// instance for the lifetime of every test class that
/// <c>IClassFixture</c>s it. Tests get a fresh per-test database name
/// from <see cref="NewDatabaseName"/> so collections / indexes from
/// one test do not leak into another, but the (slow) mongod boot is
/// amortised across the class.
///
/// epr-efp: shared so endpoint suites and service suites can drop
/// their <c>Substitute.For&lt;IWorkItemPersistence&gt;</c> wholesale
/// and run against the real driver — the same code path production
/// uses. The previous mocked implementations let regressions in BSON
/// conventions, indexes, projections (epr-4pf), case-insensitive
/// task ids (epr-aq5 / epr-gl6), optimistic concurrency and atomic
/// compound writes pass CI silently.
/// </summary>
public sealed class MongoIntegrationFixture : IAsyncLifetime
{
    private IMongoRunner? _runner;

    static MongoIntegrationFixture()
    {
        // Match production startup ordering — these are the same calls
        // Program.cs makes before any IMongoClient is constructed.
        // Idempotent, so the static cctor is safe even though the
        // fixture itself can be instantiated more than once across
        // parallel test classes.
        MongoConventions.Register();
        WorkItemBsonRegistration.Register();
    }

    public string ConnectionString =>
        _runner?.ConnectionString
        ?? throw new InvalidOperationException("Mongo runner has not started yet.");

    public async ValueTask InitializeAsync()
    {
        _runner = await MongoRunner.RunAsync(new MongoRunnerOptions
        {
            // A single-node replica set is the closest match to the CDP
            // production topology and unlocks transactions / change
            // streams should a future test need them.
            UseSingleNodeReplicaSet = true,
        });
    }

    public ValueTask DisposeAsync()
    {
        _runner?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Build a unique database name so each test gets a clean slate
    /// without paying for a fresh mongod.
    /// </summary>
    public static string NewDatabaseName(string prefix = "test") =>
        $"{prefix}_{Guid.NewGuid():N}";
}

/// <summary>
/// Minimal <see cref="IMongoDbClientFactory"/> that points at the
/// fixture's ephemeral <c>mongod</c>. Exists so tests can drive the
/// production <see cref="WorkItemPersistence"/> constructor without
/// standing up the full DI container / Options pipeline.
/// </summary>
public sealed class TestMongoDbClientFactory : IMongoDbClientFactory
{
    private readonly MongoClient _client;
    private readonly IMongoDatabase _database;

    public TestMongoDbClientFactory(string connectionString, string databaseName)
    {
        _client = new MongoClient(connectionString);
        _database = _client.GetDatabase(databaseName);
    }

    public IMongoClient GetClient() => _client;

    public IMongoCollection<T> GetCollection<T>(string collection) =>
        _database.GetCollection<T>(collection);
}
