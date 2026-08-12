using System.Diagnostics.CodeAnalysis;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-133 Mongo-backed <see cref="IAccreditationIdLookup"/>. Probes the
/// shared <c>workItems</c> collection for any document carrying the
/// supplied id in its <c>payload.accreditationId</c> field. Excluded
/// from code coverage because it is a thin Mongo adapter — mirrors the
/// pattern used by <see cref="WorkItemPersistence"/>.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class AccreditationIdLookup(
    IMongoDbClientFactory connectionFactory,
    ILoggerFactory loggerFactory)
    : MongoService<WorkItem>(connectionFactory, "workItems", loggerFactory), IAccreditationIdLookup
{
    public async Task<bool> ExistsAsync(
        string accreditationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accreditationId);

        // The $type predicate is NOT redundant with the equality, and removing
        // it silently costs a full collection scan on every approval.
        //
        // MongoDB will only use a partial index when the query predicate is
        // provably a subset of the index's partialFilterExpression, and it does
        // not infer "this equality operand is a string" from the literal. An
        // equality-only filter therefore plans as COLLSCAN against the index
        // defined below, while this one plans as IXSCAN. Verified by explain in
        // AccreditationIdLookupMongoIntegrationTests, which asserts the stage so
        // the regression cannot return unnoticed.
        var count = await Collection.CountDocumentsAsync(
            ExistsFilter(accreditationId),
            new CountOptions { Limit = 1 },
            cancellationToken);
        return count > 0;
    }

    /// <summary>
    /// Exposed so the integration test can assert the planner picks IXSCAN for
    /// the filter production actually issues, rather than for a copy of it that
    /// could drift away from this one.
    /// </summary>
    internal static FilterDefinition<WorkItem> ExistsFilter(string accreditationId) =>
        Builders<WorkItem>.Filter.And(
            Builders<WorkItem>.Filter.Eq("payload.accreditationId", accreditationId),
            Builders<WorkItem>.Filter.Type("payload.accreditationId", BsonType.String));

    protected override List<CreateIndexModel<WorkItem>> DefineIndexes(
        IndexKeysDefinitionBuilder<WorkItem> builder)
    {
        // Unique index so ExistsAsync uses an index scan rather than a full
        // collection scan, and as a DB-level backstop against two concurrent
        // approvals stamping the same accreditation id (TOCTOU).
        //
        // PARTIAL, not sparse (epr-r9oy). Sparse excludes only documents where
        // the field is ABSENT; a document carrying an EXPLICIT null is indexed
        // like any other, so the second one collides on the unique constraint.
        // That is not hypothetical: the payload merge in
        // ReAccreditationDulyMakingService round-trips through
        // ReAccreditationPayload and materialises every modelled-but-absent
        // field as an explicit null, accreditationId among them (it is null
        // until approval). Under Sparse that made the first duly making in a
        // collection succeed and every one after it fail with E11000, which
        // reached the regulator as a 500.
        //
        // A partial filter of "is a string" excludes explicit nulls and absent
        // fields alike, while keeping uniqueness over real ids. ExistsAsync
        // above must carry the same predicate for the index to be usable.
        //
        // MongoIndexReconciler retires the older Sparse copy on deploy: the key
        // spec is unchanged, so the server raises IndexKeySpecsConflict (86) and
        // the reconciler drops and recreates with these options. The rebuild is
        // clean even on a collection already full of explicit nulls, because
        // they now fall outside the filter.
        var accreditationId = new CreateIndexModel<WorkItem>(
            builder.Ascending("payload.accreditationId"),
            new CreateIndexOptions<WorkItem>
            {
                Unique = true,
                PartialFilterExpression = Builders<WorkItem>.Filter.Type(
                    "payload.accreditationId", BsonType.String),
            });
        return [accreditationId];
    }
}
