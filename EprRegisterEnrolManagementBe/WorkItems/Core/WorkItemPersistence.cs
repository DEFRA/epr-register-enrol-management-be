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
    /// Persist updates made by the engine (state transitions, assignment, notes).
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

    /// <summary>
    /// RA-311/MBE-3: find an existing work item of <paramref name="typeId"/>
    /// whose <c>payload.operatorApplicationId</c> matches
    /// <paramref name="operatorApplicationId"/>, or <c>null</c> if none
    /// exists. Backs the idempotent-submit check in
    /// <see cref="WorkItemService.SubmitAsync"/>: a caller (the operator
    /// backend, forwarding an operator's original "submit application" call)
    /// that retries after a client-side timeout must be handed back the
    /// work item created by the first attempt rather than creating a
    /// duplicate.
    /// </summary>
    Task<WorkItem?> FindByOperatorApplicationIdAsync(
        string typeId,
        string operatorApplicationId,
        CancellationToken cancellationToken = default);
}

public sealed class WorkItemPersistence : MongoService<WorkItem>, IWorkItemPersistence
{
    // Computed once: state id → workflow rank (RA-324), used by the Status sort.
    private readonly IReadOnlyDictionary<string, int> _statusRank;

    public WorkItemPersistence(
        IMongoDbClientFactory connectionFactory,
        ILoggerFactory loggerFactory,
        IWorkItemRegistry registry)
        : base(connectionFactory, "workItems", loggerFactory)
    {
        _statusRank = WorkItemSort.StatusRank(registry);
    }

    /// <summary>
    /// Test-only convenience overload that derives the registry from the
    /// shipping module set (currently re-accreditation). Production wiring
    /// always uses the registry-injecting constructor above; this keeps the
    /// many persistence-layer integration tests that predate it from having to
    /// thread a registry through, while still exercising the real Status sort
    /// ordering.
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
    public async Task<WorkItem?> FindByOperatorApplicationIdAsync(
        string typeId, string operatorApplicationId, CancellationToken cancellationToken = default)
    {
        var filter = Builders<WorkItem>.Filter.And(
            Builders<WorkItem>.Filter.Eq(w => w.TypeId, typeId),
            Builders<WorkItem>.Filter.Eq("payload.operatorApplicationId", operatorApplicationId));

        return await Collection.Find(filter).FirstOrDefaultAsync(cancellationToken);
    }

    [ExcludeFromCodeCoverage]
    public async Task<WorkItemPage> QueryAsync(WorkItemQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var filter = BuildFilter(query);

        var totalCount = await Collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken);

        var page = query.NormalisedPage;
        var pageSize = query.NormalisedPageSize;
        var skip = (page - 1) * pageSize;

        var sortStages = WorkItemSort.BuildStages(query.Sort, query.SortDescending, _statusRank);

        List<WorkItem> items;
        if (sortStages is null)
        {
            // Default path (RA-324: unchanged from the original behaviour) —
            // newest submitted first, with the per-item Notes / AuditLog
            // collections projected away (epr-4pf): the list endpoint never
            // renders them and they dominate document size on chatty items.
            var projection = Builders<WorkItem>.Projection
                .Exclude(w => w.Notes)
                .Exclude(w => w.AuditLog);

            items = await Collection
                .Find(filter)
                .Project<WorkItem>(projection)
                .SortByDescending(w => w.SubmittedAt)
                .Skip(skip)
                .Limit(pageSize)
                .ToListAsync(cancellationToken);
        }
        else
        {
            // Explicit RA-324 sort (organisation / status / due-date). Status
            // and due-date can't be expressed as a plain field sort, so an
            // aggregation computes the sort key. Two $unset stages: the first
            // drops the fat Notes / AuditLog collections (the same ones the
            // default projection excludes) BEFORE the in-memory $sort so the
            // sort buffers slim documents; the second drops the computed sort
            // scratch fields AFTER the $sort that reads them, so the result
            // still deserialises to WorkItem (which does not ignore extra BSON
            // elements).
            var (addFields, sort) = sortStages.Value;
            var serializer = MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry
                .GetSerializer<WorkItem>();
            var matchDoc = filter.Render(
                new RenderArgs<WorkItem>(serializer, MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry));

            var stages = new List<BsonDocument>
            {
                new("$match", matchDoc),
                new("$unset", new BsonArray { "notes", "auditLog" }),
            };
            if (addFields is not null)
            {
                stages.Add(new BsonDocument("$addFields", addFields));
            }
            stages.Add(new BsonDocument("$sort", sort));
            stages.Add(new BsonDocument("$unset", new BsonArray(WorkItemSort.ComputedFields)));
            stages.Add(new BsonDocument("$skip", skip));
            stages.Add(new BsonDocument("$limit", pageSize));

            PipelineDefinition<WorkItem, WorkItem> pipeline = stages;
            items = await Collection
                .Aggregate(pipeline, cancellationToken: cancellationToken)
                .ToListAsync(cancellationToken);
        }

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

        // RA-324: the Applications page merges the old separate orgName / orgId
        // inputs into ONE "Organisation name or ID" box. It matches the needle
        // case-insensitively as a substring of payload.organisationName OR
        // payload.operatorOrganisationId (e.g. ORG-123-001). Registration-id is
        // deliberately NOT matched here (dropped from the product per RA-324).
        // Uses a regex on organisationName rather than the $text index because
        // Mongo forbids OR-ing a $text clause with other conditions; fine at
        // this volume since the list is always pre-filtered by typeId/state.
        var organisation = query.NormalisedOrganisation;
        if (!string.IsNullOrEmpty(organisation))
        {
            var escaped = System.Text.RegularExpressions.Regex.Escape(organisation);
            var contains = new MongoDB.Bson.BsonRegularExpression(escaped, "i");
            clauses.Add(builder.Or(
                builder.Regex("payload.organisationName", contains),
                builder.Regex("payload.operatorOrganisationId", contains)));
        }

        // RA-324: material filter (multi-select). payload.material stores a
        // single lowercase token (plastic/glass/paper/steel/wood/aluminium/
        // fibre); match each requested value case-insensitively as an exact
        // token (anchored regex) so casing differences never hide a match, and
        // OR multiple selections together.
        if (query.Materials is { Count: > 0 } materials)
        {
            var materialClauses = materials
                .Select(m => builder.Regex(
                    "payload.material",
                    new MongoDB.Bson.BsonRegularExpression(
                        $"^{System.Text.RegularExpressions.Regex.Escape(m)}$", "i")))
                .ToList();
            clauses.Add(materialClauses.Count == 1
                ? materialClauses[0]
                : builder.Or(materialClauses));
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

        // RA-313: there is deliberately NO terminal-state exclusion here.
        //
        // RA-224 used to hide every terminal state (approved/rejected/withdrawn)
        // from the list unless IncludeArchived was set, so that the worklist
        // showed only in-flight work. That ticket was closed as incorrectly
        // filed and should never have been built: RA-313 AC01 requires a
        // withdrawn application to be visible in the regulator's worklist with
        // its "Withdrawn" status, and the same reasoning applies to the other
        // two terminal states — a regulator looking for a decided application
        // should find it where every other application is.
        //
        // WorkItemQuery.IncludeArchived is retained and still bound from the
        // query string: management-fe continues to send it and the snapshot /
        // backfill migrations pass IncludeArchived: true. It now selects
        // nothing, because nothing is excluded. See epr-kenf for retiring the
        // parameter and its "Show archived items" checkbox — deliberately NOT
        // done here, because the Applications page UI was signed off against a
        // prototype that no story captures.
        //
        // payload.archivedAt is untouched: ArchiveBackgroundService still
        // stamps it after ArchiveAfterDays, and the list still renders it as
        // "Archived: <date>" on the card. It is now purely informational.

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
        // A C# null would be written as an explicit BSON null; callers must
        // pass BsonNull.Value deliberately if that is what they mean.
        ArgumentNullException.ThrowIfNull(value);

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
        // RA-311/MBE-3: the operator backend forwards the operator's own
        // "submit application" call and may retry it after a client-side
        // timeout even though the original request already succeeded here
        // (CDP logs show OJ FE aborting at 5s while this round-trip can take
        // up to 100s). Unique + sparse on the same principle as
        // applicationReference above: only documents that actually carry
        // payload.operatorApplicationId are constrained, so items submitted
        // without one (e.g. case-management-created items, legacy items)
        // are unaffected, but two submissions carrying the same operator
        // application id can never both persist — the engine's retry-lookup
        // in WorkItemService.SubmitAsync uses this as its duplicate-key
        // signal to hand back the original work item instead of erroring.
        var operatorApplicationId = new CreateIndexModel<WorkItem>(
            builder.Ascending("payload.operatorApplicationId"),
            new CreateIndexOptions { Unique = true, Sparse = true });
        // epr-r9oy: read by AccreditationIdLookup.ExistsAsync, but DEFINED HERE
        // rather than there, because index definitions only take effect when the
        // owning MongoService is constructed. AccreditationIdLookup is a lazy
        // singleton that nothing resolves during startup — the only migration
        // that pulls it in resolves it after a feature-flag check that is off by
        // default (25a1399) — so its indexes are not created until the first
        // approval. WorkItemPersistence is resolved by
        // WorkItemMigrationHostedService on every boot, so a definition here
        // reaches every environment on deploy.
        //
        // PARTIAL, not sparse. Sparse excludes only documents where the field is
        // ABSENT; a document carrying an EXPLICIT null is indexed like any other,
        // so the second one collides on the unique constraint. That is not
        // hypothetical: ReAccreditationDulyMakingService round-trips the payload
        // through ReAccreditationPayload and merges ToBsonDocument(), which
        // materialises every modelled-but-absent field as an explicit null,
        // accreditationId among them (it is null until approval). Under Sparse
        // that made the first duly making in a collection succeed and every one
        // after it fail with E11000, which reached the regulator as a 500.
        //
        // "Is a string" excludes explicit nulls and absent fields alike while
        // keeping uniqueness over real ids, so the backstop against two
        // concurrent approvals stamping the same id survives. Note that
        // AccreditationIdLookup.ExistsFilter must carry the same $type predicate
        // for the planner to use this index at all.
        var accreditationId = new CreateIndexModel<WorkItem>(
            builder.Ascending("payload.accreditationId"),
            new CreateIndexOptions<WorkItem>
            {
                Unique = true,
                PartialFilterExpression = Builders<WorkItem>.Filter.Type(
                    "payload.accreditationId", BsonType.String),
            });
        return [typeAndSubmitted, stateAndSubmitted, submittedDescending, assigneeAndSubmitted, nationAndState, orgNameText, applicationReference, operatorApplicationId, accreditationId];
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