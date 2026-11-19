using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.Test.Utils.Mongo;

/// <summary>
/// RA-739: creating a collection inside a multi-document transaction is not
/// supported on every Mongo deployment, so the collections an add-ORS
/// submission's transaction writes to must exist before it starts. The
/// <c>submissions</c> and <c>referenceSequences</c> stores define no indexes,
/// so nothing else would create them.
/// </summary>
public sealed class MongoCollectionsTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly IMongoDatabase _database;

    public MongoCollectionsTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("collections");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _database = _factory.GetClient().GetDatabase(_databaseName);
    }

    public async ValueTask DisposeAsync() =>
        await _factory.GetClient().DropDatabaseAsync(_databaseName, Ct);

    [Fact]
    public async Task EnsureExists_creates_a_missing_collection()
    {
        MongoCollections.EnsureExists(_database, "scratch");

        Assert.Contains("scratch", await CollectionNamesAsync());
    }

    [Fact]
    public async Task EnsureExists_leaves_an_existing_collection_alone()
    {
        var scratch = _database.GetCollection<BsonDocument>("scratch");
        await scratch.InsertOneAsync(new BsonDocument("_id", "kept"), cancellationToken: Ct);

        MongoCollections.EnsureExists(_database, "scratch");
        await MongoCollections.EnsureExistsAsync(_database, "scratch", Ct);

        Assert.Equal(1, await scratch.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct));
    }

    [Fact]
    public async Task EnsureExistsAsync_creates_a_missing_collection()
    {
        await MongoCollections.EnsureExistsAsync(_database, "scratch", Ct);

        Assert.Contains("scratch", await CollectionNamesAsync());
    }

    // Several instances starting at once each try to create the collection;
    // whichever lose the race (NamespaceExists, or a no-op success on newer
    // servers) must not fail their startup.
    [Fact]
    public async Task EnsureExistsAsync_tolerates_concurrent_creators()
    {
        await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => MongoCollections.EnsureExistsAsync(_database, "scratch", Ct)));

        Assert.Contains("scratch", await CollectionNamesAsync());
    }

    [Fact]
    public async Task Constructing_the_submission_store_creates_the_submissions_collection()
    {
        _ = new MongoSubmissionStore(_factory, NullLoggerFactory.Instance);

        Assert.Contains(MongoSubmissionStore.CollectionName, await CollectionNamesAsync());
    }

    [Fact]
    public async Task Constructing_the_reference_sequence_store_creates_the_referenceSequences_collection()
    {
        _ = new MongoReferenceSequenceStore(_factory, NullLoggerFactory.Instance);

        Assert.Contains(MongoReferenceSequenceStore.CollectionName, await CollectionNamesAsync());
    }

    private async Task<List<string>> CollectionNamesAsync() =>
        await (await _database.ListCollectionNamesAsync(cancellationToken: Ct)).ToListAsync(Ct);
}
