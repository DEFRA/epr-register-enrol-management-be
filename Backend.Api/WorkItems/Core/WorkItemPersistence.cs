using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Backend.Api.Utils.Auditing;
using Backend.Api.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Backend.Api.WorkItems.Core;

/// <summary>
/// Persistence for <see cref="WorkItem"/>s. Owned by the framework so every
/// type shares a single envelope/index strategy; modules read/write their own
/// payload shape on top of it.
/// </summary>
public interface IWorkItemPersistence
{
    Task CreateAsync(WorkItem workItem, CancellationToken cancellationToken = default);

    Task<WorkItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<WorkItem>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist updates made by the engine (state transitions, task completions).
    /// Implementations replace the document in its entirety so callers can
    /// mutate any field on the supplied <see cref="WorkItem"/> before saving.
    /// </summary>
    Task ReplaceAsync(WorkItem workItem, CancellationToken cancellationToken = default);
}

[ExcludeFromCodeCoverage]
public sealed class WorkItemPersistence(IMongoDbClientFactory connectionFactory, ILoggerFactory loggerFactory)
    : MongoService<WorkItem>(connectionFactory, "workItems", loggerFactory), IWorkItemPersistence
{
    public async Task CreateAsync(WorkItem workItem, CancellationToken cancellationToken = default)
    {
        await Collection.InsertOneAsync(workItem, cancellationToken: cancellationToken);
        Logger.Audit(
            "Submitted work item {WorkItemId} of type {WorkItemTypeId} by {SubmittedBy}",
            workItem.Id, workItem.TypeId, workItem.SubmittedBy ?? "unknown");
    }

    public async Task<WorkItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await Collection
            .Find(w => w.Id == id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<WorkItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await Collection
            .Find(_ => true)
            .SortByDescending(w => w.SubmittedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task ReplaceAsync(WorkItem workItem, CancellationToken cancellationToken = default)
    {
        await Collection.ReplaceOneAsync(
            w => w.Id == workItem.Id,
            workItem,
            cancellationToken: cancellationToken);
        Logger.Audit(
            "Updated work item {WorkItemId} of type {WorkItemTypeId} now in state {WorkItemState}",
            workItem.Id, workItem.TypeId, workItem.StateId);
    }

    protected override List<CreateIndexModel<WorkItem>> DefineIndexes(
        IndexKeysDefinitionBuilder<WorkItem> builder)
    {
        var typeAndSubmitted = new CreateIndexModel<WorkItem>(
            builder.Combine(
                builder.Ascending(w => w.TypeId),
                builder.Descending(w => w.SubmittedAt)));
        return [typeAndSubmitted];
    }
}

/// <summary>
/// Conversions between API-facing <see cref="JsonElement"/> payloads and the
/// <see cref="BsonDocument"/> form persisted in MongoDB. Lifted to a static
/// helper so endpoints, tests and future modules share one implementation.
/// </summary>
public static class WorkItemPayloadConverter
{
    private static readonly BsonDocument s_emptyDocument = new();

    public static BsonDocument ToBson(JsonElement? payload)
    {
        if (!payload.HasValue || payload.Value.ValueKind == JsonValueKind.Undefined ||
            payload.Value.ValueKind == JsonValueKind.Null)
        {
            return new BsonDocument();
        }

        if (payload.Value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidWorkItemPayloadException(
                $"Work item payload must be a JSON object, got {payload.Value.ValueKind}.");
        }

        var json = payload.Value.GetRawText();
        return BsonDocument.Parse(json);
    }

    public static JsonElement ToJson(BsonDocument? document)
    {
        var bson = document ?? s_emptyDocument;
        var json = bson.ToJson();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

public sealed class InvalidWorkItemPayloadException(string message) : Exception(message);
