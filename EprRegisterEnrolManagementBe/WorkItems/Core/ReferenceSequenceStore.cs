using EprRegisterEnrolManagementBe.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Allocates monotonically increasing, never-reused sequence numbers for
/// server-generated case references (RA-492). Each <c>sequenceKey</c> (for
/// example <c>IYC-ORS-2026</c>) has its own independent counter. The first
/// number allocated for a key is 0.
/// </summary>
public interface IReferenceSequenceStore
{
    /// <summary>
    /// Atomically allocate and return the next number for
    /// <paramref name="sequenceKey"/>. Two concurrent callers never receive
    /// the same number. A number is consumed even if the caller then fails to
    /// use it, so a sequence can have gaps but never duplicates.
    /// </summary>
    Task<long> NextAsync(string sequenceKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// MongoDB-backed <see cref="IReferenceSequenceStore"/>. One document per
/// sequence key in the <see cref="CollectionName"/> collection, shaped
/// <c>{ _id: "IYC-ORS-2026", seq: NumberLong(22) }</c>, where <c>seq</c> is
/// how many numbers have been allocated (so the last one handed out is
/// <c>seq - 1</c>). Allocation is a single
/// <c>findAndModify</c> (<c>$inc</c> with upsert, returning the post-image),
/// which MongoDB applies atomically per document. Only the implicit unique
/// <c>_id</c> index is needed, because every read and write is by key.
/// </summary>
public sealed class MongoReferenceSequenceStore : MongoService<BsonDocument>, IReferenceSequenceStore
{
    /// <summary>Collection holding one counter document per sequence key.</summary>
    public const string CollectionName = "referenceSequences";

    /// <summary>Field on each counter document holding how many numbers have been allocated for the key.</summary>
    public const string SequenceField = "seq";

    /// <summary>
    /// Allocation attempts before giving up. Only the first-insert upsert race
    /// is retried. Once one racer has inserted the counter document, the
    /// retry is a plain atomic update and cannot collide again, so two
    /// attempts are enough. The third covers a counter deleted mid-race.
    /// </summary>
    internal const int MaxAllocationAttempts = 3;

    private static readonly FindOneAndUpdateOptions<BsonDocument> s_allocateOptions = new()
    {
        IsUpsert = true,
        ReturnDocument = ReturnDocument.After,
    };

    public MongoReferenceSequenceStore(
        IMongoDbClientFactory connectionFactory,
        ILoggerFactory loggerFactory
    )
        : base(connectionFactory, CollectionName, loggerFactory) { }

    public async Task<long> NextAsync(
        string sequenceKey,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sequenceKey);

        var filter = Builders<BsonDocument>.Filter.Eq("_id", sequenceKey);
        var update = Builders<BsonDocument>.Update.Inc(SequenceField, 1L);

        var attempt = 1;
        while (true)
        {
            try
            {
                var counter = await Collection.FindOneAndUpdateAsync(
                    filter,
                    update,
                    s_allocateOptions,
                    cancellationToken
                );
                // seq counts allocations, so the first allocation (seq 1) is number 0.
                return counter[SequenceField].ToInt64() - 1;
            }
            // Two first-ever allocations for the same key can both miss the
            // filter and both try to insert the counter. The loser gets a
            // duplicate key error on _id. The winner's document now exists, so
            // retrying turns into an atomic $inc and returns the next number.
            catch (Exception ex)
                when (MongoDuplicateKey.Is(ex) && attempt < MaxAllocationAttempts)
            {
                Logger.LogInformation(
                    ex,
                    "Reference sequence {SequenceKey} upsert raced on attempt {Attempt}; retrying",
                    sequenceKey,
                    attempt
                );
                attempt++;
            }
        }
    }

    protected override List<CreateIndexModel<BsonDocument>> DefineIndexes(
        IndexKeysDefinitionBuilder<BsonDocument> builder
    ) => [];
}
