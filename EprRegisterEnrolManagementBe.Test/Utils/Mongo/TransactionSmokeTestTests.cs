using EphemeralMongo;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EprRegisterEnrolManagementBe.Test.Utils.Mongo;

/// <summary>
/// RA-739: the startup transaction smoke test proves the Mongo deployment
/// supports the multi-document transactions an all-or-nothing submission
/// needs. Runs against the fixture's single-node replica set and against a
/// standalone mongod, which has no transactions.
/// </summary>
public sealed class TransactionSmokeTestTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly MongoIntegrationFixture _fixture;
    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _factory;

    public TransactionSmokeTestTests(MongoIntegrationFixture fixture)
    {
        _fixture = fixture;
        _databaseName = MongoIntegrationFixture.NewDatabaseName("smoke");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
    }

    public async ValueTask DisposeAsync() =>
        await _factory.GetClient().DropDatabaseAsync(_databaseName);

    [Fact]
    public async Task RunAsync_on_a_replica_set_passes_and_leaves_nothing_behind()
    {
        var result = await TransactionSmokeTest.RunAsync(Database(_factory), Ct);

        Assert.True(result.Supported);
        Assert.True(result.CommitWorks);
        Assert.True(result.AbortWorks);
        Assert.Null(result.ErrorKind);
        Assert.Equal(
            0,
            await _factory
                .GetCollection<BsonDocument>(TransactionSmokeTest.ScratchCollection)
                .CountDocumentsAsync(new BsonDocument(), cancellationToken: Ct)
        );
    }

    // The smoke test reads inside its transaction, as a submission will, so a
    // connection string that reads from secondaries fails it unless the
    // transaction reads from the primary (MongoTransactions.Options).
    [Fact]
    public async Task RunAsync_passes_when_the_connection_string_reads_from_secondaries()
    {
        var secondaries = new TestMongoDbClientFactory(
            new MongoUrlBuilder(_fixture.ConnectionString)
            {
                ReadPreference = ReadPreference.SecondaryPreferred,
            }.ToString(),
            _databaseName
        );

        var result = await TransactionSmokeTest.RunAsync(Database(secondaries), Ct);

        Assert.True(result.Supported);
        Assert.Null(result.ErrorKind);
    }

    [Fact]
    public async Task RunAsync_on_a_standalone_server_reports_unsupported()
    {
        using var standalone = await MongoRunner.RunAsync(
            new MongoRunnerOptions { UseSingleNodeReplicaSet = false },
            Ct
        );

        var result = await TransactionSmokeTest.RunAsync(
            new MongoClient(standalone.ConnectionString).GetDatabase("smoke"),
            Ct
        );

        Assert.False(result.Supported);
        Assert.Equal("NotSupportedException", result.ErrorKind);
    }

    [Fact]
    public async Task Hosted_service_logs_that_transactions_are_supported()
    {
        var logger = new ListLogger<TransactionSmokeTestHostedService>();

        await new TransactionSmokeTestHostedService(_factory, logger).RunAsync(Ct);

        var record = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Information, record.Level);
        Assert.Equal("Mongo transaction smoke test supported=true", record.Message);
    }

    [Fact]
    public async Task Hosted_service_logs_an_error_when_transactions_are_unsupported()
    {
        using var standalone = await MongoRunner.RunAsync(
            new MongoRunnerOptions { UseSingleNodeReplicaSet = false },
            Ct
        );
        var logger = new ListLogger<TransactionSmokeTestHostedService>();

        await new TransactionSmokeTestHostedService(
            new TestMongoDbClientFactory(standalone.ConnectionString, "smoke"),
            logger
        ).RunAsync(Ct);

        var record = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Equal(
            "Mongo transaction smoke test supported=false commit=False abort=False error=NotSupportedException",
            record.Message
        );
    }

    [Fact]
    public async Task Hosted_service_logs_an_error_with_the_exception_when_the_smoke_test_cannot_run()
    {
        var failure = new InvalidOperationException("no database");
        var factory = Substitute.For<IMongoDbClientFactory>();
        factory
            .GetCollection<BsonDocument>(TransactionSmokeTest.ScratchCollection)
            .Throws(failure);
        var logger = new ListLogger<TransactionSmokeTestHostedService>();

        await new TransactionSmokeTestHostedService(factory, logger).RunAsync(Ct);

        var record = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Same(failure, record.Exception);
        Assert.Equal("InvalidOperationException", record.Properties["ErrorKind"]);
    }

    private static IMongoDatabase Database(IMongoDbClientFactory factory) =>
        factory.GetCollection<BsonDocument>(TransactionSmokeTest.ScratchCollection).Database;
}
