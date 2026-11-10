using System.Net;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-492: sequence allocation for in-year change references, against a real
/// (ephemeral) mongod, plus a substituted collection to drive the
/// first-insert upsert race deterministically.
/// </summary>
public sealed class MongoReferenceSequenceStoreTests : IAsyncDisposable
{
    private readonly TestMongoDbClientFactory _clientFactory;
    private readonly string _databaseName;
    private readonly MongoReferenceSequenceStore _store;

    public MongoReferenceSequenceStoreTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("ref_seq");
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _store = new MongoReferenceSequenceStore(_clientFactory, NullLoggerFactory.Instance);
    }

    public async ValueTask DisposeAsync() =>
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

    [Fact]
    public async Task NextAsync_starts_at_one_and_increments_per_key()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(1, await _store.NextAsync("IYC-ORS-2026", ct));
        Assert.Equal(2, await _store.NextAsync("IYC-ORS-2026", ct));
        Assert.Equal(3, await _store.NextAsync("IYC-ORS-2026", ct));

        var counter = await _clientFactory
            .GetCollection<BsonDocument>(MongoReferenceSequenceStore.CollectionName)
            .Find(new BsonDocument("_id", "IYC-ORS-2026"))
            .SingleAsync(ct);
        Assert.Equal(3L, counter[MongoReferenceSequenceStore.SequenceField].AsInt64);
    }

    [Fact]
    public async Task NextAsync_keeps_an_independent_counter_per_change_type_and_year()
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.NextAsync("IYC-ORS-2026", ct);
        await _store.NextAsync("IYC-ORS-2026", ct);

        Assert.Equal(1, await _store.NextAsync("IYC-ORS-2027", ct));
        Assert.Equal(1, await _store.NextAsync("IYC-SIP-2026", ct));
        Assert.Equal(3, await _store.NextAsync("IYC-ORS-2026", ct));
    }

    [Fact]
    public async Task NextAsync_never_hands_the_same_number_to_concurrent_callers()
    {
        const int Callers = 50;
        var ct = TestContext.Current.CancellationToken;

        // Fresh key, so every caller also races the first-insert upsert.
        var numbers = await Task.WhenAll(
            Enumerable.Range(0, Callers).Select(_ => _store.NextAsync("IYC-ORS-2030", ct))
        );

        Assert.Equal(Enumerable.Range(1, Callers).Select(n => (long)n), numbers.Order());
    }

    [Fact]
    public async Task NextAsync_rejects_a_blank_key()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.NextAsync(" ", TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task NextAsync_retries_when_it_loses_the_first_insert_upsert_race()
    {
        var collection = SubstituteCollection();
        collection
            .FindOneAndUpdateAsync(
                Arg.Any<FilterDefinition<BsonDocument>>(),
                Arg.Any<UpdateDefinition<BsonDocument>>(),
                Arg.Any<FindOneAndUpdateOptions<BsonDocument, BsonDocument>>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                _ => throw DuplicateKeyCommandException(),
                _ => Task.FromResult(new BsonDocument { ["_id"] = "IYC-ORS-2026", ["seq"] = 2L })
            );

        var store = new MongoReferenceSequenceStore(
            FactoryFor(collection),
            NullLoggerFactory.Instance
        );

        Assert.Equal(2, await store.NextAsync("IYC-ORS-2026", TestContext.Current.CancellationToken));
        await collection
            .Received(2)
            .FindOneAndUpdateAsync(
                Arg.Any<FilterDefinition<BsonDocument>>(),
                Arg.Any<UpdateDefinition<BsonDocument>>(),
                Arg.Any<FindOneAndUpdateOptions<BsonDocument, BsonDocument>>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task NextAsync_gives_up_after_repeated_duplicate_keys()
    {
        var collection = SubstituteCollection();
        collection
            .FindOneAndUpdateAsync(
                Arg.Any<FilterDefinition<BsonDocument>>(),
                Arg.Any<UpdateDefinition<BsonDocument>>(),
                Arg.Any<FindOneAndUpdateOptions<BsonDocument, BsonDocument>>(),
                Arg.Any<CancellationToken>()
            )
            .ThrowsAsync(DuplicateKeyCommandException());

        var store = new MongoReferenceSequenceStore(
            FactoryFor(collection),
            NullLoggerFactory.Instance
        );

        await Assert.ThrowsAsync<MongoCommandException>(() =>
            store.NextAsync("IYC-ORS-2026", TestContext.Current.CancellationToken)
        );
        await collection
            .Received(MongoReferenceSequenceStore.MaxAllocationAttempts)
            .FindOneAndUpdateAsync(
                Arg.Any<FilterDefinition<BsonDocument>>(),
                Arg.Any<UpdateDefinition<BsonDocument>>(),
                Arg.Any<FindOneAndUpdateOptions<BsonDocument, BsonDocument>>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task NextAsync_does_not_retry_a_non_duplicate_key_failure()
    {
        var collection = SubstituteCollection();
        collection
            .FindOneAndUpdateAsync(
                Arg.Any<FilterDefinition<BsonDocument>>(),
                Arg.Any<UpdateDefinition<BsonDocument>>(),
                Arg.Any<FindOneAndUpdateOptions<BsonDocument, BsonDocument>>(),
                Arg.Any<CancellationToken>()
            )
            .ThrowsAsync(CommandException(code: 50));

        var store = new MongoReferenceSequenceStore(
            FactoryFor(collection),
            NullLoggerFactory.Instance
        );

        await Assert.ThrowsAsync<MongoCommandException>(() =>
            store.NextAsync("IYC-ORS-2026", TestContext.Current.CancellationToken)
        );
        await collection
            .Received(1)
            .FindOneAndUpdateAsync(
                Arg.Any<FilterDefinition<BsonDocument>>(),
                Arg.Any<UpdateDefinition<BsonDocument>>(),
                Arg.Any<FindOneAndUpdateOptions<BsonDocument, BsonDocument>>(),
                Arg.Any<CancellationToken>()
            );
    }

    private static IMongoCollection<BsonDocument> SubstituteCollection() =>
        Substitute.For<IMongoCollection<BsonDocument>>();

    private static IMongoDbClientFactory FactoryFor(IMongoCollection<BsonDocument> collection)
    {
        var factory = Substitute.For<IMongoDbClientFactory>();
        factory.GetCollection<BsonDocument>(MongoReferenceSequenceStore.CollectionName).Returns(collection);
        return factory;
    }

    private static MongoCommandException DuplicateKeyCommandException() =>
        CommandException(code: 11000);

    private static MongoCommandException CommandException(int code) =>
        new(
            new ConnectionId(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
            "findAndModify failed",
            new BsonDocument("findAndModify", MongoReferenceSequenceStore.CollectionName),
            new BsonDocument { ["ok"] = 0, ["code"] = code, ["errmsg"] = "E" + code }
        );
}
