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

    /// <summary>
    /// Set a single named field inside <see cref="WorkItem.Payload"/>, leaving
    /// every other field of the document byte-for-byte untouched. Returns
    /// <c>true</c> when a document was matched, <c>false</c> when no work item
    /// with that id exists.
    ///
    /// <para>
    /// Prefer this over load → mutate → <see cref="ReplaceAsync"/> whenever a
    /// module needs to stamp one payload field. A full replace round-trips the
    /// payload through the module's typed model, which MATERIALISES modelled-
    /// but-absent fields as explicit nulls. That is not cosmetic: the
    /// <c>payload.accreditationId</c> index is unique + <em>sparse</em>, and a
    /// sparse index excludes only documents where the field is ABSENT — so
    /// writing an explicit null pulls the document into the index and the
    /// second such write anywhere in the collection fails with a duplicate-key
    /// error (RA-291). A targeted <c>$set</c> cannot resurrect that class of
    /// bug for any modelled-but-absent field, now or in future.
    /// </para>
    ///
    /// <para>
    /// Deliberately does NOT participate in the <see cref="WorkItem.Version"/>
    /// optimistic-concurrency protocol and does not touch
    /// <see cref="WorkItem.LastModifiedAt"/>: it is a single-field write that
    /// cannot clobber a concurrent writer's changes to any other field, so
    /// taking part in the version dance would only manufacture spurious
    /// conflicts. Callers that need the version bumped should follow up with
    /// a normal engine operation.
    /// </para>
    /// </summary>
    Task<bool> SetPayloadFieldAsync(
        Guid workItemId,
        string fieldName,
        BsonValue value,
        CancellationToken cancellationToken = default);
}

public sealed class WorkItemPersistence : MongoService<WorkItem>, IWorkItemPersistence
{
    // Computed once: the distinct terminal state ids across every registered
    // type (RA-224). Used to hide finished work (approved/rejected/withdrawn)
    // from the active worklist by default.
    private readonly IReadOnlySet<string> _terminalStateIds;

    public WorkItemPersistence(
        IMongoDbClientFactory connectionFactory,
        ILoggerFactory loggerFactory,
        IWorkItemRegistry registry)
        : base(connectionFactory, "workItems", loggerFactory)
    {
        _terminalStateIds = TerminalStates.Ids(registry);
    }

    /// <summary>
    /// Test-only convenience overload that derives the terminal-state set from
    /// the shipping module set (currently re-accreditation). Production wiring
    /// always uses the registry-injecting constructor above; this keeps the
    /// many persistence-layer integration tests that predate RA-224 from
    /// having to thread a registry through, while still exercising the real
    /// terminal-state behaviour.
    /// </summary>
    [ExcludeFromCodeCoverage]
    internal WorkItemPersistence(
        IMongoDbClientFactory connectionFactory,
        ILoggerFactory loggerFactory)
        : this(
            connectionFactory,
            loggerFactory,
            new WorkItemRegistry([new ReAccreditation.ReAccreditationType()]))
    {
    }
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

        var filter = BuildFilter(query, _terminalStateIds);

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

    internal static FilterDefinition<WorkItem> BuildFilter(
        WorkItemQuery query, IReadOnlySet<string> terminalStateIds)
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

        var orgId = query.NormalisedOrgId;
        if (!string.IsNullOrEmpty(orgId))
        {
            var escaped = System.Text.RegularExpressions.Regex.Escape(orgId);
            var pattern = new MongoDB.Bson.BsonRegularExpression(escaped, "i");
            clauses.Add(builder.Regex("payload.applicationReference", pattern));
        }

        var registrationId = query.NormalisedRegistrationId;
        if (!string.IsNullOrEmpty(registrationId))
        {
            var escaped = System.Text.RegularExpressions.Regex.Escape(registrationId);
            var pattern = new MongoDB.Bson.BsonRegularExpression(escaped, "i");
            clauses.Add(builder.Regex("_id", pattern));
        }

        var orgName = query.NormalisedOrgName;
        if (!string.IsNullOrEmpty(orgName))
        {
            // Wrap in quotes for phrase matching: prevents OR word-matching where common
            // words like "Org" in the query accidentally match unrelated items.
            clauses.Add(builder.Text($"\"{orgName}\"", new TextSearchOptions { CaseSensitive = false }));
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
            // Filter by payload.nation stored as a string in the BSON document
            // (the Nation enum is serialised as its member name, e.g. "England").
            clauses.Add(builder.In("payload.nation", nations));
        }

        // Archive exclusion: hide finished work in any terminal state
        // (approved/rejected/withdrawn) by default so the active worklist stays
        // focused on in-flight work (RA-224). Pass IncludeArchived=true to reveal
        // them (e.g. for the "Show archived" filter or background jobs).
        //
        // Any terminal state the caller explicitly requested via StateIds is
        // left in place — combining $in:[X] with $nin:[X,...] on the same field
        // would make the query unsatisfiable (matches nothing). So we only
        // exclude terminal states that were NOT explicitly requested.
        if (!query.IncludeArchived && terminalStateIds.Count > 0)
        {
            var toExclude = terminalStateIds
                .Where(id => !(query.StateIds?.Contains(id, StringComparer.OrdinalIgnoreCase) ?? false))
                .ToList();
            if (toExclude.Count > 0)
            {
                clauses.Add(builder.Nin(w => w.StateId, toExclude));
            }
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

    public async Task<bool> SetPayloadFieldAsync(
        Guid workItemId,
        string fieldName,
        BsonValue value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);

        // Guard the dotted-path injection: a caller passing "a.b" or a "$"
        // operator would target a nested document or rewrite a different part
        // of the envelope entirely. This method's contract is one field
        // directly under `payload`.
        if (fieldName.Contains('.', StringComparison.Ordinal)
            || fieldName.StartsWith('$'))
        {
            throw new ArgumentException(
                "Payload field name must be a single field directly under 'payload' " +
                "(no dotted paths, no update operators).",
                nameof(fieldName));
        }

        var result = await Collection.UpdateOneAsync(
            Builders<WorkItem>.Filter.Eq(w => w.Id, workItemId),
            Builders<WorkItem>.Update.Set($"payload.{fieldName}", value),
            cancellationToken: cancellationToken);

        return result.MatchedCount > 0;
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
                builder.Ascending("payload.nation"),
                builder.Ascending(w => w.StateId)));
        // Search by org name: text index supports word-level case-insensitive $text queries.
        // Only one text index is allowed per collection; scope it to organisationName.
        var orgNameText = new CreateIndexModel<WorkItem>(
            builder.Text("payload.organisationName"));
        // Search by org ID / applicationReference: ascending index lets anchored prefix
        // regex queries avoid a full collection scan.
        //
        // RA-219: the backend now owns reference generation, so the index is
        // UNIQUE to enforce one applicationReference per work item and to give
        // the engine a duplicate-key signal to retry on. It is SPARSE so legacy
        // documents that predate server-side generation (and therefore have no
        // payload.applicationReference) are simply not indexed and cannot trip
        // the unique constraint — only documents that actually carry the field
        // are constrained, and every new submission sets it.
        var applicationReference = new CreateIndexModel<WorkItem>(
            builder.Ascending("payload.applicationReference"),
            new CreateIndexOptions { Unique = true, Sparse = true });
        return [typeAndSubmitted, stateAndSubmitted, submittedDescending, assigneeAndSubmitted, nationAndState, orgNameText, applicationReference];
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