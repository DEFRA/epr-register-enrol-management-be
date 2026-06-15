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

    /// <summary>MongoDB server error code for a duplicate-key violation (<c>E11000</c>).</summary>
    private const int DuplicateKeyErrorCode = 11000;

    /// <summary>
    /// Create the supplied indexes, recovering from two classes of deploy-time
    /// failure:
    /// <list type="bullet">
    ///   <item>An <c>IndexOptionsConflict</c> (an existing index with the same
    ///   key but different options) — resolved by dropping the conflicting
    ///   copy and retrying.</item>
    ///   <item>A duplicate-key failure building a <em>unique</em> index over
    ///   data that already contains duplicates (<c>E11000</c>) — resolved by
    ///   invoking <paramref name="resolveDuplicateData"/>, if supplied, to
    ///   reconcile the offending documents, then retrying. This is what lets a
    ///   deploy self-heal an environment whose collection predates a newly
    ///   tightened unique index, rather than crash-looping on startup.</item>
    /// </list>
    /// </summary>
    /// <param name="resolveDuplicateData">
    /// Optional callback invoked with the duplicate-key exception when a unique
    /// index cannot build over existing data. It should make the offending
    /// field unique (e.g. reassign duplicate values) and return <c>true</c>
    /// when it changed something — the index build is then retried once.
    /// Returning <c>false</c> (or supplying no callback) lets the original
    /// failure propagate. The callback must only resolve duplicates it
    /// understands and rethrow / return <c>false</c> for any other index.
    /// </param>
    /// <returns>The names of any indexes that were dropped to resolve a conflict.</returns>
    public static IReadOnlyList<string> EnsureIndexes<T>(
        IMongoCollection<T> collection,
        IReadOnlyList<CreateIndexModel<T>> models,
        ILogger logger,
        Func<MongoCommandException, bool>? resolveDuplicateData = null)
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
            CreateManyResolvingDuplicateData(collection, models, logger, resolveDuplicateData);
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
            // recreated with the desired options. The recreate can itself trip
            // a unique constraint over pre-existing data, so route it through
            // the same duplicate-data resolution.
            CreateManyResolvingDuplicateData(collection, models, logger, resolveDuplicateData);
            return dropped;
        }
    }

    /// <summary>
    /// Run <c>CreateMany</c>; if it fails with a duplicate-key error building a
    /// unique index, give <paramref name="resolveDuplicateData"/> a chance to
    /// reconcile the data and retry exactly once. The resolver is responsible
    /// for generating replacement values that cannot re-collide, so a single
    /// retry suffices.
    /// </summary>
    private static void CreateManyResolvingDuplicateData<T>(
        IMongoCollection<T> collection,
        IReadOnlyList<CreateIndexModel<T>> models,
        ILogger logger,
        Func<MongoCommandException, bool>? resolveDuplicateData)
    {
        try
        {
            collection.Indexes.CreateMany(models);
        }
        catch (MongoCommandException ex)
            when (resolveDuplicateData is not null && IsDuplicateKeyBuildFailure(ex))
        {
            logger.LogWarning(
                ex,
                "Unique index build failed on collection {Collection} because the data contains duplicates; "
                + "attempting to reconcile the data and retry.",
                collection.CollectionNamespace.CollectionName);

            if (!resolveDuplicateData(ex))
            {
                // The resolver did not recognise / could not fix this
                // duplicate, so the failure must surface unchanged.
                throw;
            }

            collection.Indexes.CreateMany(models);
        }
    }

    /// <summary>
    /// True when the command failure means an index with the same key already
    /// exists but with a different definition (options or spec), which we can
    /// resolve by dropping and recreating. See <see cref="s_conflictCodes"/>.
    /// </summary>
    private static bool IsIndexDefinitionConflict(MongoCommandException ex) =>
        s_conflictCodes.Contains(ex.Code);

    /// <summary>
    /// True when a <c>createIndexes</c> command failed because building a
    /// unique index would violate uniqueness against data already in the
    /// collection (<c>E11000</c>). The server surfaces this as the command's
    /// error code; older/driver variants only set it in the message, so check
    /// both.
    /// </summary>
    private static bool IsDuplicateKeyBuildFailure(MongoCommandException ex) =>
        ex.Code == DuplicateKeyErrorCode
        || (ex.Message?.Contains("E11000", StringComparison.Ordinal) ?? false);

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
