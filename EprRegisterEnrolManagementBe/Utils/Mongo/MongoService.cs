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
        // RA-739: create the collection up front. A collection with no
        // indexes of its own (e.g. submissions, referenceSequences) would
        // otherwise be created implicitly by its first write, which can be
        // inside a submission's transaction, and creating a collection inside
        // a transaction is not supported on every Mongo deployment.
        MongoCollections.EnsureExists(Collection.Database, collectionName);
        EnsureIndexes();
    }

    protected abstract List<CreateIndexModel<T>> DefineIndexes(IndexKeysDefinitionBuilder<T> builder);

    /// <summary>
    /// Names of indexes this collection no longer wants (RA-739). They are
    /// dropped, if present, before the desired indexes are created, so an
    /// index whose key changed does not linger beside its replacement.
    /// </summary>
    protected virtual IReadOnlyCollection<string> ObsoleteIndexNames => [];

    protected void EnsureIndexes()
    {
        MongoIndexReconciler.DropObsoleteIndexes(Collection, ObsoleteIndexNames, Logger);

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
        MongoIndexReconciler.EnsureIndexes(Collection, indexes, Logger);
    }
}