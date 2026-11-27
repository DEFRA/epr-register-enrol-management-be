using System.Diagnostics.CodeAnalysis;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-739: one stored submission, written in the same transaction as the work
/// items it created. <see cref="OperatorRequestId"/> is the document's
/// <c>_id</c>, so it is also the replay guard: a repeated or concurrent
/// submission with the same id finds or collides with it.
/// RA-767: <see cref="OperatorOrganisationId"/> is the submitting
/// organisation, so a later group of the same operator request can be checked
/// against it before it shares <see cref="SubmissionReference"/>, and
/// <see cref="SubmissionSize"/> is the size stamped on its work items, which a
/// later group sharing the reference must agree with. Both are <c>null</c> on
/// a record stored before RA-767.
/// </summary>
public sealed record SubmissionRecord(
    string OperatorRequestId,
    string TypeId,
    string SubmissionReference,
    IReadOnlyList<SubmissionRecordItem> WorkItems,
    DateTime SubmittedAt,
    string? OperatorOrganisationId,
    int? SubmissionSize
);

/// <summary>RA-739: the work item created for one site of a <see cref="SubmissionRecord"/>.</summary>
public sealed record SubmissionRecordItem(
    BsonValue SiteId,
    Guid WorkItemId,
    string ApplicationReference
);

/// <summary>RA-739: the <c>submissions</c> collection.</summary>
public interface ISubmissionStore
{
    /// <summary>The stored submission for <paramref name="operatorRequestId"/>, or <c>null</c>.</summary>
    Task<SubmissionRecord?> FindAsync(
        string operatorRequestId,
        CancellationToken cancellationToken = default
    );

    /// <summary>As <see cref="FindAsync(string, CancellationToken)"/>, reading inside <paramref name="session"/>.</summary>
    Task<SubmissionRecord?> FindAsync(
        IClientSessionHandle session,
        string operatorRequestId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// RA-767: every stored submission whose <see cref="SubmissionRecord.SubmissionReference"/>
    /// is <paramref name="submissionReference"/>, read inside <paramref name="session"/>:
    /// the groups of one operator request that share it.
    /// </summary>
    Task<IReadOnlyList<SubmissionRecord>> FindBySubmissionReferenceAsync(
        IClientSessionHandle session,
        string submissionReference,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Insert <paramref name="record"/> in <paramref name="session"/>. A record
    /// with the same <see cref="SubmissionRecord.OperatorRequestId"/> already
    /// on file raises a duplicate-key error.
    /// </summary>
    Task InsertAsync(
        IClientSessionHandle session,
        SubmissionRecord record,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// MongoDB-backed <see cref="ISubmissionStore"/>. One document per submission,
/// shaped <c>{ _id: operatorRequestId, typeId, submissionReference,
/// workItems: [{ siteId, workItemId, applicationReference }], submittedAt,
/// operatorOrganisationId, submissionSize }</c>.
/// Besides the implicit unique <c>_id</c> index, a non-unique one on
/// <c>submissionReference</c> backs <see cref="FindBySubmissionReferenceAsync"/>
/// (RA-767: the groups of one request share a reference). The collection is
/// created when the store is constructed (see <see cref="MongoService{T}"/>),
/// so the first submission's transaction never has to create it.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class MongoSubmissionStore : MongoService<BsonDocument>, ISubmissionStore
{
    /// <summary>Collection holding one document per submission.</summary>
    public const string CollectionName = "submissions";

    public MongoSubmissionStore(
        IMongoDbClientFactory connectionFactory,
        ILoggerFactory loggerFactory
    )
        : base(connectionFactory, CollectionName, loggerFactory) { }

    public async Task<SubmissionRecord?> FindAsync(
        string operatorRequestId,
        CancellationToken cancellationToken = default
    )
    {
        // A replay can arrive straight after the first submission committed,
        // so read from the primary, never a lagging secondary.
        var document = await Collection
            .WithReadPreference(ReadPreference.Primary)
            .Find(ById(operatorRequestId))
            .FirstOrDefaultAsync(cancellationToken);
        return document is null ? null : FromDocument(document);
    }

    public async Task<SubmissionRecord?> FindAsync(
        IClientSessionHandle session,
        string operatorRequestId,
        CancellationToken cancellationToken = default
    )
    {
        var document = await Collection
            .Find(session, ById(operatorRequestId))
            .FirstOrDefaultAsync(cancellationToken);
        return document is null ? null : FromDocument(document);
    }

    public async Task<IReadOnlyList<SubmissionRecord>> FindBySubmissionReferenceAsync(
        IClientSessionHandle session,
        string submissionReference,
        CancellationToken cancellationToken = default
    )
    {
        var documents = await Collection
            .Find(session, Builders<BsonDocument>.Filter.Eq(SubmissionReferenceKey, submissionReference))
            .ToListAsync(cancellationToken);
        return documents.Select(FromDocument).ToList();
    }

    public Task InsertAsync(
        IClientSessionHandle session,
        SubmissionRecord record,
        CancellationToken cancellationToken = default
    ) =>
        Collection.InsertOneAsync(
            session,
            ToDocument(record),
            cancellationToken: cancellationToken
        );

    protected override List<CreateIndexModel<BsonDocument>> DefineIndexes(
        IndexKeysDefinitionBuilder<BsonDocument> builder
    ) => [new CreateIndexModel<BsonDocument>(builder.Ascending(SubmissionReferenceKey))];

    private const string SubmissionReferenceKey = "submissionReference";

    private static FilterDefinition<BsonDocument> ById(string operatorRequestId) =>
        Builders<BsonDocument>.Filter.Eq("_id", operatorRequestId);

    private static BsonDocument ToDocument(SubmissionRecord record) =>
        new()
        {
            ["_id"] = record.OperatorRequestId,
            ["typeId"] = record.TypeId,
            [SubmissionReferenceKey] = record.SubmissionReference,
            ["workItems"] = new BsonArray(
                record.WorkItems.Select(item => new BsonDocument
                {
                    ["siteId"] = item.SiteId,
                    ["workItemId"] = item.WorkItemId.ToString(),
                    ["applicationReference"] = item.ApplicationReference,
                })
            ),
            ["submittedAt"] = record.SubmittedAt,
            ["operatorOrganisationId"] = record.OperatorOrganisationId is { } organisationId
                ? organisationId
                : BsonNull.Value,
            ["submissionSize"] = record.SubmissionSize is { } size ? size : BsonNull.Value,
        };

    private static SubmissionRecord FromDocument(BsonDocument document) =>
        new(
            document["_id"].AsString,
            document["typeId"].AsString,
            document[SubmissionReferenceKey].AsString,
            document["workItems"]
                .AsBsonArray.Select(value => value.AsBsonDocument)
                .Select(item => new SubmissionRecordItem(
                    item["siteId"],
                    Guid.Parse(item["workItemId"].AsString),
                    item["applicationReference"].AsString
                ))
                .ToList(),
            document["submittedAt"].ToUniversalTime(),
            document.TryGetValue("operatorOrganisationId", out var organisationId) && organisationId.IsString
                ? organisationId.AsString
                : null,
            document.TryGetValue("submissionSize", out var size) && size.IsInt32 ? size.AsInt32 : null
        );
}
