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

    /// <summary>
    /// Deliberately empty (epr-r9oy). The <c>payload.accreditationId</c> index
    /// this service reads is defined by
    /// <see cref="WorkItemPersistence.DefineIndexes"/> instead.
    ///
    /// <para>
    /// Defining it here does not work: <c>MongoService</c> creates indexes from
    /// its constructor, and this type is a lazily constructed singleton that
    /// nothing resolves during startup. The only migration that pulls it in
    /// resolves <c>IAccreditationIdGenerator</c> after a feature-flag check that
    /// is off by default — deliberately, since eager resolution once broke every
    /// WebApplicationFactory test's host bootstrap (25a1399). So an index
    /// declared here is not created until the first approval, which means a
    /// changed definition does not reach a deployed environment on deploy. It
    /// belongs on the <see cref="WorkItemPersistence"/> that owns the same
    /// collection and IS constructed on every boot.
    /// </para>
    ///
    /// <para>
    /// Left as an explicit empty override rather than deleted so this reasoning
    /// sits where the next person looks for it — the abstract member has to be
    /// implemented regardless.
    /// </para>
    /// </summary>
    protected override List<CreateIndexModel<WorkItem>> DefineIndexes(
        IndexKeysDefinitionBuilder<WorkItem> builder) => [];
}
