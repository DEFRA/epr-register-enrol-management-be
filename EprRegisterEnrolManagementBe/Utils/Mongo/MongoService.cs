using MongoDB.Driver;
using System.Diagnostics.CodeAnalysis;

namespace EprRegisterEnrolManagementBe.Utils.Mongo;

[ExcludeFromCodeCoverage]
public abstract class MongoService<T>
{
    protected readonly IMongoClient Client;
    protected readonly IMongoCollection<T> Collection;

    protected readonly ILogger Logger;

    protected MongoService(IMongoDbClientFactory connectionFactory, string collectionName, ILoggerFactory loggerFactory)
    {
        Client = connectionFactory.GetClient();
        Collection = connectionFactory.GetCollection<T>(collectionName);
        var loggerName = GetType().FullName ?? GetType().Name;
        Logger = loggerFactory.CreateLogger(loggerName);
        EnsureIndexes();
    }

    protected abstract List<CreateIndexModel<T>> DefineIndexes(IndexKeysDefinitionBuilder<T> builder);

    /// <summary>
    /// Reconcile existing data so a <em>unique</em> index can build when the
    /// collection already contains duplicate values for that index's key
    /// (a <c>E11000</c> failure during <c>EnsureIndexes</c>). The base
    /// implementation does nothing and lets the failure propagate; a subclass
    /// that owns a unique index over potentially-dirty legacy data overrides
    /// this to make the offending field unique (e.g. reassign duplicates) and
    /// returns <c>true</c> when it changed something so the build is retried.
    /// </summary>
    protected virtual bool TryResolveDuplicateData(MongoCommandException ex) => false;

    protected void EnsureIndexes()
    {
        var builder = Builders<T>.IndexKeys;
        var indexes = DefineIndexes(builder);
        if (indexes.Count == 0) return;

        Logger.LogInformation(
            "Ensuring index is created if it does not exist for collection {CollectionNamespaceCollectionName} in DB {DatabaseDatabaseNamespace}",
            Collection.CollectionNamespace.CollectionName,
            Collection.Database.DatabaseNamespace);

        // Delegate to the reconciler so a deploy that tightens an existing
        // index's options (e.g. RA-219 making payload.applicationReference
        // unique+sparse) does not break startup with an IndexOptionsConflict
        // against the older copy still on the server.
        MongoIndexReconciler.EnsureIndexes(Collection, indexes, Logger, TryResolveDuplicateData);
    }
}