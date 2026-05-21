using System.Diagnostics.CodeAnalysis;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;
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

        var filter = Builders<WorkItem>.Filter.Eq("payload.accreditationId", accreditationId);
        var count = await Collection
            .CountDocumentsAsync(filter, new CountOptions { Limit = 1 }, cancellationToken);
        return count > 0;
    }

    protected override List<CreateIndexModel<WorkItem>> DefineIndexes(
        IndexKeysDefinitionBuilder<WorkItem> builder) => [];
}
