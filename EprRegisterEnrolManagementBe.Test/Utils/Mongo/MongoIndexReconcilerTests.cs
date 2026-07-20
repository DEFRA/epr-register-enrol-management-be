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
}
