using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// epr-r9oy: real-Mongo coverage for the <c>payload.accreditationId</c> index
/// that <see cref="AccreditationIdLookup"/> owns.
///
/// <para>
/// Every test drives the index through the <see cref="AccreditationIdLookup"/>
/// CONSTRUCTOR rather than creating it by hand, because the constructor is what
/// creates it in production (<c>MongoService</c> calls <c>EnsureIndexes</c>) and
/// because a test that hand-rolls the index proves nothing about the definition
/// the service actually ships. It also means the reconcile-on-deploy path is
/// exercised for real: the old copy is created first, and the constructor is
/// left to retire it.
/// </para>
///
/// <para>
/// Ephemeral MongoDB rather than a mock throughout, because the entire defect
/// lives in semantics only the server implements — what <c>Sparse</c> does and
/// does not exclude, and when the planner will use a partial index.
/// </para>
/// </summary>
public sealed class AccreditationIdLookupMongoIntegrationTests
    : IAsyncDisposable
{
    private const string CollectionName = "workItems";
    private const string IndexName = "payload.accreditationId_1";

    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _clientFactory;
    private readonly IMongoCollection<BsonDocument> _raw;

    public AccreditationIdLookupMongoIntegrationTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("accreditation_id_lookup");
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _raw = _clientFactory.GetCollection<BsonDocument>(CollectionName);
    }

    private AccreditationIdLookup CreateLookup() =>
        new(_clientFactory, NullLoggerFactory.Instance);

    /// <summary>
    /// The defect, pinned against the OLD definition so the fix is demonstrably
    /// fixing something. Two documents carrying an explicit null — exactly what
    /// the duly-making payload merge writes — and the second is rejected.
    /// </summary>
    [Fact]
    public async Task TheOldSparseIndexRejectsASecondExplicitNull()
    {
        await CreateLegacySparseIndexAsync();

        await _raw.InsertOneAsync(WorkItemWithAccreditationId(BsonNull.Value));

        var second = async () =>
            await _raw.InsertOneAsync(WorkItemWithAccreditationId(BsonNull.Value));

        var error = await Assert.ThrowsAsync<MongoWriteException>(second);
        Assert.Equal(ServerErrorCategory.DuplicateKey, error.WriteError.Category);
    }

    [Fact]
    public async Task ExplicitNullsCoexistUnderThePartialIndex()
    {
        CreateLookup();

        await _raw.InsertOneAsync(WorkItemWithAccreditationId(BsonNull.Value));
        await _raw.InsertOneAsync(WorkItemWithAccreditationId(BsonNull.Value));

        Assert.Equal(2, await _raw.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    /// <summary>
    /// The deploy path onto an environment already carrying the old definition:
    /// the reconciler must retire the Sparse copy and leave the partial one.
    /// </summary>
    [Fact]
    public async Task ReplacesTheLegacySparseIndexOnConstruction()
    {
        await CreateLegacySparseIndexAsync();

        CreateLookup();

        var index = await FindIndexAsync();
        Assert.NotNull(index);
        Assert.False(index!.Contains("sparse"));
        Assert.True(index.Contains("partialFilterExpression"));
    }

    /// <summary>
    /// The rebuild happens on a collection whose documents already carry the
    /// explicit nulls written before the fix. Under a unique + sparse
    /// definition that build fails with 11000 and
    /// <c>MongoIndexReconciler</c> skips the index — losing the uniqueness
    /// backstop silently. The partial filter must make it clean.
    /// </summary>
    [Fact]
    public async Task RebuildsCleanlyOverDocumentsThatAlreadyCarryExplicitNulls()
    {
        await CreateLegacySparseIndexAsync();
        await _raw.InsertOneAsync(WorkItemWithAccreditationId(BsonNull.Value));
        await _raw.Indexes.DropOneAsync(IndexName);
        await _raw.InsertOneAsync(WorkItemWithAccreditationId(BsonNull.Value));
        await _raw.InsertOneAsync(WorkItemWithAccreditationId(BsonNull.Value));

        CreateLookup();

        Assert.NotNull(await FindIndexAsync());
    }

    /// <summary>
    /// The reason the index exists at all: two concurrent approvals must not be
    /// able to stamp the same accreditation id.
    /// </summary>
    [Fact]
    public async Task StillRejectsDuplicateRealAccreditationIds()
    {
        CreateLookup();

        await _raw.InsertOneAsync(WorkItemWithAccreditationId("EA26AB1234"));

        var duplicate = async () =>
            await _raw.InsertOneAsync(WorkItemWithAccreditationId("EA26AB1234"));

        var error = await Assert.ThrowsAsync<MongoWriteException>(duplicate);
        Assert.Equal(ServerErrorCategory.DuplicateKey, error.WriteError.Category);
    }

    [Theory]
    [InlineData("EA26AB1234", true)]
    [InlineData("EA26ZZ9999", false)]
    public async Task ExistsAsyncFindsOnlyRealIds(string probe, bool expected)
    {
        var lookup = CreateLookup();
        await _raw.InsertOneAsync(WorkItemWithAccreditationId("EA26AB1234"));
        await _raw.InsertOneAsync(WorkItemWithAccreditationId(BsonNull.Value));

        Assert.Equal(expected, await lookup.ExistsAsync(probe, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The trap that comes with a partial index: MongoDB uses one only when the
    /// query predicate is provably a subset of the partial filter, and it does
    /// not infer "string" from an equality literal. Drop the $type predicate
    /// from <see cref="AccreditationIdLookup.ExistsFilter"/> and this plans as
    /// COLLSCAN — correct results, full scan of workItems on every approval.
    /// Asserting the stage is the only thing that catches that.
    /// </summary>
    [Fact]
    public async Task ExistsAsyncUsesAnIndexScanNotACollectionScan()
    {
        CreateLookup();

        var stages = await WinningPlanStagesAsync(AccreditationIdLookup.ExistsFilter("EA26AB1234"));

        Assert.Contains("IXSCAN", stages);
        Assert.DoesNotContain("COLLSCAN", stages);
    }

    /// <summary>
    /// The pre-fix definition: unique + sparse, unnamed, so the server derives
    /// <see cref="IndexName"/> — byte-for-byte what deployed environments carry.
    /// </summary>
    private async Task CreateLegacySparseIndexAsync() =>
        await _raw.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("payload.accreditationId"),
                new CreateIndexOptions { Unique = true, Sparse = true }));

    private static BsonDocument WorkItemWithAccreditationId(BsonValue accreditationId) =>
        new()
        {
            ["_id"] = Guid.NewGuid().ToString(),
            ["payload"] = new BsonDocument { ["accreditationId"] = accreditationId },
        };

    private async Task<BsonDocument?> FindIndexAsync()
    {
        using var cursor = await _raw.Indexes.ListAsync();
        var indexes = await cursor.ToListAsync();
        return indexes.FirstOrDefault(index =>
            index.GetValue("name", BsonNull.Value).ToString() == IndexName);
    }

    /// <summary>
    /// Render the production filter and ask the server to explain a find that
    /// uses it, then collect every stage name in the winning plan.
    /// </summary>
    private async Task<IReadOnlyList<string>> WinningPlanStagesAsync(
        FilterDefinition<WorkItem> filter)
    {
        var typed = _clientFactory.GetCollection<WorkItem>(CollectionName);
        var rendered = filter.Render(
            new RenderArgs<WorkItem>(typed.DocumentSerializer, BsonSerializer.SerializerRegistry));

        var database = _clientFactory.GetClient().GetDatabase(_databaseName);
        var explain = await database.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                ["explain"] = new BsonDocument
                {
                    ["find"] = CollectionName,
                    ["filter"] = rendered,
                },
                ["verbosity"] = "queryPlanner",
            });

        var stages = new List<string>();
        CollectStages(explain["queryPlanner"]["winningPlan"].AsBsonDocument, stages);
        return stages;
    }

    private static void CollectStages(BsonValue node, List<string> stages)
    {
        if (node is BsonDocument document)
        {
            if (document.TryGetValue("stage", out var stage) && stage.IsString)
            {
                stages.Add(stage.AsString);
            }

            foreach (var element in document)
            {
                CollectStages(element.Value, stages);
            }
        }
        else if (node is BsonArray array)
        {
            foreach (var item in array)
            {
                CollectStages(item, stages);
            }
        }
    }

    public async ValueTask DisposeAsync() =>
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);
}
