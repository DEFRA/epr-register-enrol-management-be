using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// epr-0nv: the one-shot corrective migration that de-duplicates
/// <c>payload.applicationReference</c> at startup so the unique index can build
/// on an environment that already holds duplicate (legacy) references.
/// Exercised against real ephemeral Mongo so the aggregation, the audit-log
/// shape and the subsequent unique-index build are all real.
/// </summary>
public sealed class ApplicationReferenceDeduplicationMigrationTests
    : IClassFixture<MongoIntegrationFixture>, IAsyncDisposable
{
    private static readonly DateTime s_now = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly MongoIntegrationFixture _fixture;
    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _factory;
    private readonly IMongoCollection<BsonDocument> _raw;

    public ApplicationReferenceDeduplicationMigrationTests(MongoIntegrationFixture fixture)
    {
        _fixture = fixture;
        _databaseName = MongoIntegrationFixture.NewDatabaseName("appref_dedupe");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _raw = _factory.GetCollection<BsonDocument>("workItems");
    }

    public async ValueTask DisposeAsync() =>
        await _factory.GetClient().DropDatabaseAsync(_databaseName);

    private static BsonDocument WorkItemDoc(string reference, DateTime submittedAt) => new()
    {
        ["_id"] = Guid.NewGuid().ToString(),
        ["typeId"] = "re-accreditation",
        ["stateId"] = "submitted",
        ["submittedAt"] = submittedAt,
        ["lastModifiedAt"] = submittedAt,
        ["auditLog"] = new BsonArray(),
        ["payload"] = new BsonDocument { ["applicationReference"] = reference },
    };

    [Fact]
    public async Task RunAsync_dedupes_duplicates_keeping_the_oldest_and_unblocks_the_unique_index()
    {
        var ct = TestContext.Current.CancellationToken;

        // Two docs sharing a reference (the older must keep it), a control doc
        // with a distinct reference, and a reference-less doc that must be left
        // alone. No unique index exists yet, so the duplicate insert is allowed.
        var older = WorkItemDoc("RA-2024-00123", s_now);
        var newer = WorkItemDoc("RA-2024-00123", s_now.AddMinutes(5));
        var control = WorkItemDoc("RA-555555555", s_now);
        var refless = WorkItemDoc("placeholder", s_now);
        refless["payload"] = new BsonDocument(); // no applicationReference
        await _raw.InsertManyAsync([older, newer, control, refless], cancellationToken: ct);

        var reassigned = await ApplicationReferenceDeduplicationMigration.RunAsync(
            _raw, NullLogger.Instance, ct);

        Assert.Equal(1, reassigned);

        // Nothing deleted.
        Assert.Equal(4, await _raw.CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Empty, cancellationToken: ct));

        var keptOlder = await GetByIdAsync(older["_id"].AsString, ct);
        var fixedNewer = await GetByIdAsync(newer["_id"].AsString, ct);
        var keptControl = await GetByIdAsync(control["_id"].AsString, ct);

        // Oldest keeps the original reference; the newer is reassigned a fresh
        // canonical value and gains an audit entry.
        Assert.Equal("RA-2024-00123", keptOlder["payload"]["applicationReference"].AsString);
        var newReference = fixedNewer["payload"]["applicationReference"].AsString;
        Assert.NotEqual("RA-2024-00123", newReference);
        Assert.Matches(@"^RA-\d{9}$", newReference);
        Assert.Contains(
            fixedNewer["auditLog"].AsBsonArray,
            e => e["action"].AsString == "application-reference-reassigned");

        // Control reference is untouched.
        Assert.Equal("RA-555555555", keptControl["payload"]["applicationReference"].AsString);

        // The unique index now builds cleanly in the persistence ctor — the
        // whole point of running the migration first.
        var ex = Record.Exception(() =>
            new WorkItemPersistence(_factory, NullLoggerFactory.Instance));
        Assert.Null(ex);
    }

    [Fact]
    public async Task RunAsync_is_a_noop_when_references_are_already_unique()
    {
        var ct = TestContext.Current.CancellationToken;
        await _raw.InsertManyAsync(
            [WorkItemDoc("RA-111111111", s_now), WorkItemDoc("RA-222222222", s_now)],
            cancellationToken: ct);

        var reassigned = await ApplicationReferenceDeduplicationMigration.RunAsync(
            _raw, NullLogger.Instance, ct);

        Assert.Equal(0, reassigned);
    }

    private async Task<BsonDocument> GetByIdAsync(string id, CancellationToken ct) =>
        await _raw.Find(Builders<BsonDocument>.Filter.Eq("_id", id)).SingleAsync(ct);
}
