using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Persistence for <see cref="WorkItem"/>s. Owned by the framework so every
/// type shares a single envelope/index strategy; modules read/write their own
/// payload shape on top of it.
/// </summary>
public interface IWorkItemPersistence
{
    Task CreateAsync(WorkItem workItem, CancellationToken cancellationToken = default);

    /// <summary>
    /// Insert <paramref name="workItem"/> only if no document with the
    /// same <see cref="WorkItem.Id"/> exists. Returns <c>true</c> when
    /// the document was inserted and <c>false</c> when an item with
    /// that id already existed (the on-disk document is left
    /// untouched). The check is atomic — it relies on the unique
    /// <c>_id</c> index, so two callers racing with the same id
    /// produce exactly one insert and one <c>false</c> regardless of
    /// timing (epr-33c).
    /// </summary>
    Task<bool> CreateIfAbsentAsync(WorkItem workItem, CancellationToken cancellationToken = default);

    Task<WorkItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Return a single page of work items matching <paramref name="query"/>,
    /// most-recently-submitted first, together with the total number of
    /// matches across every page.
    ///
    /// The per-item <see cref="WorkItem.Notes"/> and
    /// <see cref="WorkItem.AuditLog"/> collections are excluded server-side
    /// (epr-4pf). The returned <see cref="WorkItem"/> instances therefore
    /// carry empty <c>Notes</c> / <c>AuditLog</c> lists regardless of
    /// what is on disk; callers that need the full timeline must
    /// <see cref="GetByIdAsync"/> the item individually. This keeps the
    /// list path's bandwidth bounded by the document envelope rather
    /// than by accumulated assessor activity.
    /// </summary>
    Task<WorkItemPage> QueryAsync(WorkItemQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist updates made by the engine (state transitions, task completions).
    /// Implementations replace the document in its entirety so callers can
    /// mutate any field on the supplied <see cref="WorkItem"/> before saving.
    /// </summary>
    Task ReplaceAsync(WorkItem workItem, CancellationToken cancellationToken = default);
}

public sealed class WorkItemPersistence(IMongoDbClientFactory connectionFactory, ILoggerFactory loggerFactory)
    : MongoService<WorkItem>(connectionFactory, "workItems", loggerFactory), IWorkItemPersistence
{
    [ExcludeFromCodeCoverage]
    public async Task CreateAsync(WorkItem workItem, CancellationToken cancellationToken = default)
    {
        await Collection.InsertOneAsync(workItem, cancellationToken: cancellationToken);
        Logger.LogInformation(
            "Submitted work item {WorkItemId} of type {WorkItemTypeId} by {SubmittedBy}",
            workItem.Id, workItem.TypeId, workItem.SubmittedBy ?? "unknown");
    }

    public async Task<bool> CreateIfAbsentAsync(
        WorkItem workItem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        try
        {
            await Collection.InsertOneAsync(workItem, cancellationToken: cancellationToken);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // _id already in the collection — another instance won the
            // race or the seeder has already run on this database.
            // Either way the caller treats this as a successful no-op.
            return false;
        }
    }

    [ExcludeFromCodeCoverage]
    public async Task<WorkItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await Collection
            .Find(w => w.Id == id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    [ExcludeFromCodeCoverage]
    public async Task<WorkItemPage> QueryAsync(WorkItemQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var filter = BuildFilter(query);

        // Project away the per-item Notes and AuditLog collections
        // (epr-4pf): the list endpoint never renders them and they
        // dominate document size on chatty items. The deserialiser
        // re-runs against the trimmed BSON and falls back to the
        // List<>'s default initialiser for the missing fields, so
        // returned WorkItem instances carry empty Notes / AuditLog
        // regardless of what is on disk.
        var projection = Builders<WorkItem>.Projection
            .Exclude(w => w.Notes)
            .Exclude(w => w.AuditLog);

        var find = Collection
            .Find(filter)
            .Project<WorkItem>(projection)
            .SortByDescending(w => w.SubmittedAt);

        var totalCount = await Collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken);

        var page = query.NormalisedPage;
        var pageSize = query.NormalisedPageSize;
        var skip = (page - 1) * pageSize;

        var items = await find
            .Skip(skip)
            .Limit(pageSize)
            .ToListAsync(cancellationToken);

        return new WorkItemPage(items, totalCount, page, pageSize);
    }

    internal static FilterDefinition<WorkItem> BuildFilter(WorkItemQuery query)
    {
        var builder = Builders<WorkItem>.Filter;
        var clauses = new List<FilterDefinition<WorkItem>>();

        if (query.TypeIds is { Count: > 0 } typeIds)
        {
            clauses.Add(builder.In(w => w.TypeId, typeIds));
        }

        if (query.StateIds is { Count: > 0 } stateIds)
        {
            clauses.Add(builder.In(w => w.StateId, stateIds));
        }

        var search = query.NormalisedSearch;
        if (!string.IsNullOrEmpty(search))
        {
            // Case-insensitive substring on submitter, plus prefix match on the
            // string-serialised id (which lets a user paste a full or partial
            // id into the search box).
            var escaped = System.Text.RegularExpressions.Regex.Escape(search);
            var pattern = new MongoDB.Bson.BsonRegularExpression(escaped, "i");

            clauses.Add(builder.Or(
                builder.Regex("_id", pattern),
                builder.Regex(nameof(WorkItem.SubmittedBy), pattern)));
        }

        var assigneeId = query.NormalisedAssigneeId;
        if (assigneeId is not null && query.UnassignedOnly)
        {
            // "Show me my work and anything still up for grabs" — assigned to
            // the user OR unassigned.
            clauses.Add(builder.Or(
                builder.Eq(w => w.AssignedToId, assigneeId),
                builder.Eq(w => w.AssignedToId, null)));
        }
        else if (assigneeId is not null)
        {
            clauses.Add(builder.Eq(w => w.AssignedToId, assigneeId));
        }
        else if (query.UnassignedOnly)
        {
            clauses.Add(builder.Eq(w => w.AssignedToId, null));
        }

        var submittedBy = query.NormalisedSubmittedBy;
        if (submittedBy is not null)
        {
            clauses.Add(builder.Eq(w => w.SubmittedBy, submittedBy));
        }

        if (query.Nations is { Count: > 0 } nations)
        {
            // Filter by payload.Nation stored as a string in the BSON document
            // (the Nation enum is serialised as its member name, e.g. "England").
            clauses.Add(builder.In("payload.Nation", nations));
        }

        return clauses.Count == 0 ? builder.Empty : builder.And(clauses);
    }

    [ExcludeFromCodeCoverage]
    public async Task ReplaceAsync(WorkItem workItem, CancellationToken cancellationToken = default)
    {
        var expectedVersion = workItem.Version;
        workItem.Version = expectedVersion + 1;

        var result = await Collection.ReplaceOneAsync(
            w => w.Id == workItem.Id && w.Version == expectedVersion,
            workItem,
            cancellationToken: cancellationToken);

        if (result.MatchedCount != 1)
        {
            // Roll the in-memory version back so a caller that catches and
            // retries does not double-increment.
            workItem.Version = expectedVersion;
            throw new WorkItemConcurrencyException(workItem.Id, expectedVersion);
        }

        Logger.LogInformation(
            "Updated work item {WorkItemId} of type {WorkItemTypeId} now in state {WorkItemState} (version {Version})",
            workItem.Id, workItem.TypeId, workItem.StateId, workItem.Version);
    }

    [ExcludeFromCodeCoverage]
    protected override List<CreateIndexModel<WorkItem>> DefineIndexes(
        IndexKeysDefinitionBuilder<WorkItem> builder)
    {
        var typeAndSubmitted = new CreateIndexModel<WorkItem>(
            builder.Combine(
                builder.Ascending(w => w.TypeId),
                builder.Descending(w => w.SubmittedAt)));
        var stateAndSubmitted = new CreateIndexModel<WorkItem>(
            builder.Combine(
                builder.Ascending(w => w.StateId),
                builder.Descending(w => w.SubmittedAt)));
        var submittedDescending = new CreateIndexModel<WorkItem>(
            builder.Descending(w => w.SubmittedAt));
        var assigneeAndSubmitted = new CreateIndexModel<WorkItem>(
            builder.Combine(
                builder.Ascending(w => w.AssignedToId),
                builder.Descending(w => w.SubmittedAt)));
        // RA-125: nation-based routing filter; most useful when also
        // filtering by state so both fields appear in the compound key.
        var nationAndState = new CreateIndexModel<WorkItem>(
            builder.Combine(
                builder.Ascending("payload.Nation"),
                builder.Ascending(w => w.StateId)));
        return [typeAndSubmitted, stateAndSubmitted, submittedDescending, assigneeAndSubmitted, nationAndState];
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

    /// <summary>
    /// Pinned BSON-to-JSON output mode for every payload we hand to API
    /// consumers. Relaxed extended JSON keeps int/long/double/decimal as
    /// plain JSON numbers and emits dates as <c>{ "$date": "ISO-8601" }</c>,
    /// so frontends see a stable shape regardless of driver version
    /// defaults (epr-b0x).
    /// </summary>
    private static readonly JsonWriterSettings s_jsonWriterSettings = new()
    {
        OutputMode = JsonOutputMode.RelaxedExtendedJson,
    };

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
        var json = bson.ToJson(s_jsonWriterSettings);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

public sealed class InvalidWorkItemPayloadException(string message) : Exception(message);