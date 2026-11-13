using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.Utils.Mongo;

/// <summary>
/// Creates a collection up front if it does not exist yet (RA-739). Creating
/// a collection inside a multi-document transaction is not supported on every
/// Mongo deployment, so any collection a transaction may be the first to write
/// to must exist before the transaction starts. A concurrent instance creating
/// it at the same moment is tolerated.
/// </summary>
public static class MongoCollections
{
    private const string NamespaceExists = "NamespaceExists";

    /// <summary>Create <paramref name="collectionName"/> in <paramref name="database"/> unless it already exists.</summary>
    public static void EnsureExists(IMongoDatabase database, string collectionName)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

        if (database.ListCollectionNames(FilterFor(collectionName)).Any())
        {
            return;
        }

        try
        {
            database.CreateCollection(collectionName);
        }
        // Another instance starting at the same time created it first. Newer
        // servers return success instead when the options match.
        catch (MongoCommandException ex) when (ex.CodeName == NamespaceExists) { }
    }

    /// <summary>As <see cref="EnsureExists(IMongoDatabase, string)"/>, asynchronously.</summary>
    public static async Task EnsureExistsAsync(
        IMongoDatabase database,
        string collectionName,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

        using (
            var cursor = await database.ListCollectionNamesAsync(
                FilterFor(collectionName),
                cancellationToken
            )
        )
        {
            if (await cursor.AnyAsync(cancellationToken))
            {
                return;
            }
        }

        try
        {
            await database.CreateCollectionAsync(collectionName, cancellationToken: cancellationToken);
        }
        // Another instance starting at the same time created it first. Newer
        // servers return success instead when the options match.
        catch (MongoCommandException ex) when (ex.CodeName == NamespaceExists) { }
    }

    private static ListCollectionNamesOptions FilterFor(string collectionName) =>
        new() { Filter = new BsonDocument("name", collectionName) };
}
