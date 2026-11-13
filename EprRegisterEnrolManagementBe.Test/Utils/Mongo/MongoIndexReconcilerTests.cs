using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.Test.Utils.Mongo;

/// <summary>
/// RA-219 PR review: focused coverage of <see cref="MongoIndexReconciler"/>
/// against a real (ephemeral) Mongo, exercising the branches the
/// <see cref="WorkItems.Core.WorkItemPersistence"/> integration tests do not:
/// the empty-model short-circuit, leaving an unrelated index untouched while
/// reconciling a conflict, and detecting the conflict by its code name.
/// RA-739 adds <see cref="MongoIndexReconciler.DropObsoleteIndexes{T}"/>,
/// which drops the RA-492 <c>payload.operatorRequestId</c> index by name.
/// </summary>
public sealed class MongoIndexReconcilerTests
    : IDisposable
{
    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly IMongoCollection<WorkItem> _collection;

    public MongoIndexReconcilerTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("reconciler");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _collection = _factory.GetCollection<WorkItem>("workItems");
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    [Fact]
    public void EnsureIndexes_with_no_models_is_a_no_op()
    {
        var dropped = MongoIndexReconciler.EnsureIndexes(
            _collection, Array.Empty<CreateIndexModel<WorkItem>>(), NullLogger.Instance);

        Assert.Empty(dropped);
    }

    [Fact]
    public void RenderKeys_renders_each_models_key_specification()
    {
        var models = new List<CreateIndexModel<WorkItem>>
        {
            new(Builders<WorkItem>.IndexKeys.Ascending("payload.applicationReference")),
        };

        var keys = MongoIndexReconciler.RenderKeys(_collection, models);

        var key = Assert.Single(keys);
        Assert.Equal(1, key["payload.applicationReference"].AsInt32);
    }

    [Fact]
    public async Task EnsureIndexes_reconciles_the_conflict_and_leaves_unrelated_indexes_untouched()
    {
        // An unrelated index that the desired set does NOT mention must be
        // preserved across the reconcile (exercises the "key not in desired
        // set" branch of DropConflictingIndexes).
        _collection.Indexes.CreateOne(
            new CreateIndexModel<WorkItem>(
                Builders<WorkItem>.IndexKeys.Ascending(w => w.SubmittedBy)),
            cancellationToken: TestContext.Current.CancellationToken);

        // The OLD, non-unique applicationReference index that will conflict.
        _collection.Indexes.CreateOne(
            new CreateIndexModel<WorkItem>(
                Builders<WorkItem>.IndexKeys.Ascending("payload.applicationReference"),
                new CreateIndexOptions { Unique = false }),
            cancellationToken: TestContext.Current.CancellationToken);

        // Desired: only the tightened unique applicationReference index.
        var desired = new List<CreateIndexModel<WorkItem>>
        {
            new(
                Builders<WorkItem>.IndexKeys.Ascending("payload.applicationReference"),
                new CreateIndexOptions { Unique = true, Sparse = true }),
        };

        var dropped = MongoIndexReconciler.EnsureIndexes(
            _collection, desired, NullLogger.Instance);

        Assert.Contains(dropped, n => n.Contains("applicationReference"));

        var indexes = await (await _collection.Indexes.ListAsync(TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);

        // The unrelated SubmittedBy index survived.
        Assert.Contains(indexes, i => i["key"].AsBsonDocument.Contains("submittedBy"));

        // The applicationReference index is now unique + sparse.
        var appRef = indexes.Single(i =>
            i["key"].AsBsonDocument.Contains("payload.applicationReference"));
        Assert.True(appRef.GetValue("unique", false).ToBoolean());
        Assert.True(appRef.GetValue("sparse", false).ToBoolean());
    }

    /// <summary>
    /// PR #72 review (RA-311): building a unique index over a field that
    /// already carries duplicate values fails with a duplicate-key error
    /// (code 11000) rather than the options-conflict codes handled above.
    /// Left unhandled this propagates out of <c>EnsureIndexes</c> and, since
    /// it runs from the <see cref="MongoService{T}"/> constructor, crashes
    /// service startup for any environment that already has the duplicates
    /// the new constraint is meant to prevent. This asserts the fallback
    /// instead: the dirty index is skipped (with an error logged), while an
    /// unrelated clean index in the same batch is still created.
    /// </summary>
    [Fact]
    public async Task EnsureIndexes_skips_a_unique_index_that_existing_duplicates_violate()
    {
        var duplicateDocs = new[]
        {
            new WorkItem
            {
                TypeId = "test-type",
                StateId = "state-1",
                Payload = new BsonDocument { ["operatorApplicationId"] = "dup-1" },
            },
            new WorkItem
            {
                TypeId = "test-type",
                StateId = "state-1",
                Payload = new BsonDocument { ["operatorApplicationId"] = "dup-1" },
            },
        };
        await _collection.InsertManyAsync(duplicateDocs, cancellationToken: TestContext.Current.CancellationToken);

        var desired = new List<CreateIndexModel<WorkItem>>
        {
            new(Builders<WorkItem>.IndexKeys.Ascending(w => w.SubmittedBy)),
            new(
                Builders<WorkItem>.IndexKeys.Ascending("payload.operatorApplicationId"),
                new CreateIndexOptions { Unique = true, Sparse = true }),
        };

        var dropped = MongoIndexReconciler.EnsureIndexes(
            _collection, desired, NullLogger.Instance);

        Assert.Empty(dropped);

        var indexes = await (await _collection.Indexes.ListAsync(TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Contains(indexes, i => i["key"].AsBsonDocument.Contains("submittedBy"));
        Assert.DoesNotContain(indexes, i => i["key"].AsBsonDocument.Contains("payload.operatorApplicationId"));
    }

    /// <summary>
    /// RA-739: the RA-492 single-field unique index on
    /// <c>payload.operatorRequestId</c> has a different key from its
    /// (operatorRequestId, site.siteId) replacement, so it never conflicts and
    /// must be dropped by name, or every site after the first in an add-ORS
    /// submission collides on it.
    /// </summary>
    [Fact]
    public async Task DropObsoleteIndexes_drops_the_legacy_operatorRequestId_index()
    {
        await CreateLegacyOperatorRequestIdIndexAsync();

        var dropped = MongoIndexReconciler.DropObsoleteIndexes(
            _collection, [WorkItemPersistence.LegacyOperatorRequestIdIndexName], NullLogger.Instance);

        Assert.Equal([WorkItemPersistence.LegacyOperatorRequestIdIndexName], dropped);
        Assert.DoesNotContain(
            await IndexNamesAsync(), name => name == WorkItemPersistence.LegacyOperatorRequestIdIndexName);
    }

    [Fact]
    public async Task DropObsoleteIndexes_is_a_no_op_when_the_legacy_index_is_absent()
    {
        _collection.Indexes.CreateOne(
            new CreateIndexModel<WorkItem>(Builders<WorkItem>.IndexKeys.Ascending(w => w.SubmittedBy)),
            cancellationToken: TestContext.Current.CancellationToken);
        var before = await IndexNamesAsync();

        var dropped = MongoIndexReconciler.DropObsoleteIndexes(
            _collection, [WorkItemPersistence.LegacyOperatorRequestIdIndexName], NullLogger.Instance);

        Assert.Empty(dropped);
        Assert.Equal(before, await IndexNamesAsync());
    }

    [Fact]
    public void DropObsoleteIndexes_is_a_no_op_when_the_collection_does_not_exist()
    {
        var dropped = MongoIndexReconciler.DropObsoleteIndexes(
            _collection, [WorkItemPersistence.LegacyOperatorRequestIdIndexName], NullLogger.Instance);

        Assert.Empty(dropped);
    }

    [Fact]
    public async Task DropObsoleteIndexes_leaves_other_indexes_alone()
    {
        await CreateLegacyOperatorRequestIdIndexAsync();
        // The replacement shares the operatorRequestId prefix but has its own
        // name, so dropping the legacy index by name must not touch it.
        _collection.Indexes.CreateOne(
            new CreateIndexModel<WorkItem>(
                Builders<WorkItem>.IndexKeys.Combine(
                    Builders<WorkItem>.IndexKeys.Ascending("payload.operatorRequestId"),
                    Builders<WorkItem>.IndexKeys.Ascending("payload.site.siteId"))),
            cancellationToken: TestContext.Current.CancellationToken);
        _collection.Indexes.CreateOne(
            new CreateIndexModel<WorkItem>(Builders<WorkItem>.IndexKeys.Ascending(w => w.SubmittedBy)),
            cancellationToken: TestContext.Current.CancellationToken);

        MongoIndexReconciler.DropObsoleteIndexes(
            _collection, [WorkItemPersistence.LegacyOperatorRequestIdIndexName], NullLogger.Instance);

        Assert.Equal(
            ["_id_", "payload.operatorRequestId_1_payload.site.siteId_1", "submittedBy_1"],
            (await IndexNamesAsync()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void DropObsoleteIndexes_never_drops_the_id_index()
    {
        _collection.InsertOne(
            new WorkItem { TypeId = "test-type", StateId = "state-1" },
            cancellationToken: TestContext.Current.CancellationToken);

        var dropped = MongoIndexReconciler.DropObsoleteIndexes(_collection, ["_id_"], NullLogger.Instance);

        Assert.Empty(dropped);
    }

    /// <summary>
    /// RA-739: constructing <see cref="WorkItemPersistence"/> (as startup does)
    /// against a database that still carries the RA-492 index leaves only the
    /// per-site replacement on <c>payload.operatorRequestId</c>.
    /// </summary>
    [Fact]
    public async Task Startup_replaces_the_legacy_operatorRequestId_index_with_the_per_site_one()
    {
        await CreateLegacyOperatorRequestIdIndexAsync();

        _ = new WorkItemPersistence(_factory, NullLoggerFactory.Instance);

        var indexes = await (await _collection.Indexes.ListAsync(TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);
        var operatorRequestId = Assert.Single(
            indexes, i => i["key"].AsBsonDocument.Contains("payload.operatorRequestId"));
        Assert.Equal(
            new BsonDocument { ["payload.operatorRequestId"] = 1, ["payload.site.siteId"] = 1 },
            operatorRequestId["key"].AsBsonDocument);
        Assert.True(operatorRequestId["unique"].ToBoolean());
    }

    private Task CreateLegacyOperatorRequestIdIndexAsync() =>
        _collection.Indexes.CreateOneAsync(
            new CreateIndexModel<WorkItem>(
                Builders<WorkItem>.IndexKeys.Ascending("payload.operatorRequestId"),
                new CreateIndexOptions<WorkItem>
                {
                    Unique = true,
                    PartialFilterExpression = Builders<WorkItem>.Filter.Type(
                        "payload.operatorRequestId", BsonType.String),
                }),
            cancellationToken: TestContext.Current.CancellationToken);

    private async Task<List<string>> IndexNamesAsync()
    {
        var indexes = await (await _collection.Indexes.ListAsync(TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);
        return indexes.Select(i => i["name"].AsString).ToList();
    }
}
