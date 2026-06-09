using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.Utils.Mongo;

/// <summary>
/// Creates a collection's indexes idempotently, tolerating the case where an
/// index with the <em>same key</em> but <em>different options</em> already
/// exists on the server.
///
/// <para>
/// MongoDB treats two indexes with an identical key specification but
/// differing options (e.g. a non-unique index vs. a unique one) as a
/// conflict: <c>CreateMany</c> fails the whole batch with server error code
/// 85 (<c>IndexOptionsConflict</c>). This is exactly what happens on deploy
/// when an environment already carries an older index definition that we have
/// since tightened (RA-219 changed <c>payload.applicationReference</c> from
/// non-unique to unique+sparse, where RA-196 had shipped the non-unique form).
/// Left unhandled it breaks service startup, because <c>EnsureIndexes</c> runs
/// in the persistence constructor.
/// </para>
///
/// <para>
/// The reconcile is intentionally <em>general</em> — it works for any
/// <see cref="MongoService{T}"/> subclass — and conservative: it only drops an
/// existing index when a desired index has the same key but the server's copy
/// has different options, and it never touches the implicit <c>_id_</c> index.
/// Every drop / recreate is logged so the deploy that performed the migration
/// is auditable.
/// </para>
/// </summary>
public static class MongoIndexReconciler
{
    /// <summary>The implicit primary-key index, which must never be dropped.</summary>
    private const string IdIndexName = "_id_";

    /// <summary>
    /// MongoDB server error codes that mean "an index with this key already
    /// exists but with a definition that differs from the one requested":
    /// <list type="bullet">
    ///   <item><c>85</c> — <c>IndexOptionsConflict</c>: same key + name, different options.</item>
    ///   <item><c>86</c> — <c>IndexKeySpecsConflict</c>: same key/auto-generated name, different spec
    ///   (the variant the server actually raises when an unnamed index's options change,
    ///   because the auto-derived name collides — e.g. <c>payload.applicationReference_1</c>).</item>
    /// </list>
    /// Both are resolved the same way: drop the existing copy and recreate.
    /// </summary>
    private static readonly int[] s_conflictCodes = [85, 86];

    /// <summary>
    /// Render every desired index model down to its key <see cref="BsonDocument"/>
    /// using the collection's document serializer, so callers do not have to
    /// reimplement the serializer-registry plumbing.
    /// </summary>
    public static IReadOnlyList<BsonDocument> RenderKeys<T>(
        IMongoCollection<T> collection,
        IReadOnlyList<CreateIndexModel<T>> models)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(models);

        var args = new RenderArgs<T>(
            collection.DocumentSerializer,
            BsonSerializer.SerializerRegistry);
        return models.Select(m => m.Keys.Render(args)).ToList();
    }

    /// <summary>
    /// Create the supplied indexes, recovering from an
    /// <c>IndexOptionsConflict</c> by dropping the conflicting existing index
    /// (matched on key spec) and retrying once.
    /// </summary>
    /// <returns>The names of any indexes that were dropped to resolve a conflict.</returns>
    public static IReadOnlyList<string> EnsureIndexes<T>(
        IMongoCollection<T> collection,
        IReadOnlyList<CreateIndexModel<T>> models,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(logger);

        if (models.Count == 0)
        {
            return Array.Empty<string>();
        }

        try
        {
            collection.Indexes.CreateMany(models);
            return Array.Empty<string>();
        }
        catch (MongoCommandException ex) when (IsIndexDefinitionConflict(ex))
        {
            logger.LogWarning(
                ex,
                "Index options conflict on collection {Collection}; reconciling by dropping conflicting indexes.",
                collection.CollectionNamespace.CollectionName);

            var dropped = DropConflictingIndexes(collection, models, logger);

            // Re-run the full batch now the conflicting copies are gone. Any
            // indexes that already matched are no-ops; the reconciled ones are
            // recreated with the desired options.
            collection.Indexes.CreateMany(models);
            return dropped;
        }
    }

    /// <summary>
    /// True when the command failure means an index with the same key already
    /// exists but with a different definition (options or spec), which we can
    /// resolve by dropping and recreating. See <see cref="s_conflictCodes"/>.
    /// </summary>
    private static bool IsIndexDefinitionConflict(MongoCommandException ex) =>
        s_conflictCodes.Contains(ex.Code);

    private static IReadOnlyList<string> DropConflictingIndexes<T>(
        IMongoCollection<T> collection,
        IReadOnlyList<CreateIndexModel<T>> models,
        ILogger logger)
    {
        var desiredKeys = RenderKeys(collection, models);

        var existing = collection.Indexes.List().ToList();
        var dropped = new List<string>();

        foreach (var index in existing)
        {
            var name = index.GetValue("name", BsonNull.Value).AsString;
            if (string.Equals(name, IdIndexName, StringComparison.Ordinal))
            {
                continue;
            }

            var existingKey = index["key"].AsBsonDocument;

            // Drop any existing index whose key matches one we intend to
            // create. CreateMany is then free to recreate it with the desired
            // options. Indexes whose key we are not (re)defining are left
            // untouched.
            if (desiredKeys.Any(desired => desired.Equals(existingKey)))
            {
                logger.LogWarning(
                    "Dropping index {IndexName} on {Collection} to reconcile changed index options.",
                    name,
                    collection.CollectionNamespace.CollectionName);
                collection.Indexes.DropOne(name);
                dropped.Add(name);
            }
        }

        return dropped;
    }
}
