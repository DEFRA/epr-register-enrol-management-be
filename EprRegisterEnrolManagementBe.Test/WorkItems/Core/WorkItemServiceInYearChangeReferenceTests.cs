using System.Security.Claims;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-492: <see cref="WorkItemService.SubmitAsync"/> gives in-year change work
/// items an <c>IYC-{code}-{yyyy}-{seq:D5}</c> reference. Runs end to end
/// against ephemeral MongoDB with the real persistence, generator and
/// sequence store. No <c>add-overseas-site</c> module is registered in
/// production yet, so these tests use a test type with that id to prove
/// the reference format does not depend on the module.
/// </summary>
public sealed class WorkItemServiceInYearChangeReferenceTests : IAsyncDisposable
{
    private const string AddOverseasSiteTypeId = "add-overseas-site";
    private const string IycOrsPattern = @"^IYC-ORS-2026-\d{5}$";

    private static readonly DateTimeOffset s_now = new(2027, 1, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly TestMongoDbClientFactory _clientFactory;
    private readonly string _databaseName;
    private readonly WorkItemPersistence _persistence;
    private readonly MongoReferenceSequenceStore _sequenceStore;
    private readonly FakeTimeProvider _time = new(s_now);

    public WorkItemServiceInYearChangeReferenceTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("svc_iyc");
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new WorkItemPersistence(_clientFactory, NullLoggerFactory.Instance);
        _sequenceStore = new MongoReferenceSequenceStore(_clientFactory, NullLoggerFactory.Instance);
    }

    public async ValueTask DisposeAsync() =>
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

    private static TestWorkItemType AddOverseasSiteType() =>
        new(AddOverseasSiteTypeId, "Add overseas reprocessing site");

    private WorkItemService BuildService(
        IWorkItemType type,
        IInYearChangeReferenceGenerator? inYearChangeReferenceGenerator = null
    ) =>
        new(
            new WorkItemRegistry([type]),
            _persistence,
            NullLogger<WorkItemService>.Instance,
            _time,
            inYearChangeReferenceGenerator: inYearChangeReferenceGenerator
                ?? new InYearChangeReferenceGenerator(_sequenceStore, _time)
        );

    private Task<WorkItemActionResult> SubmitAsync(
        WorkItemService service,
        IWorkItemType type,
        BsonDocument payload
    ) =>
        service.SubmitAsync(
            type,
            payload,
            "test-client",
            User(),
            cancellationToken: TestContext.Current.CancellationToken
        );

    private async Task<WorkItem> GetAsync(Guid id)
    {
        var fetched = await _persistence.GetByIdAsync(id, TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        return fetched!;
    }

    private async Task<long?> CounterAsync(string sequenceKey)
    {
        var counter = await _clientFactory
            .GetCollection<BsonDocument>(MongoReferenceSequenceStore.CollectionName)
            .Find(new BsonDocument("_id", sequenceKey))
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        return counter?[MongoReferenceSequenceStore.SequenceField].ToInt64();
    }

    private static ClaimsPrincipal User() =>
        new(
            new ClaimsIdentity(
                [
                    new Claim("client_id", "test-client"),
                    new Claim("user:id", "alice-1"),
                    new Claim("user:name", "Alice Example"),
                ],
                "test"
            )
        );

    [Fact]
    public async Task Submit_add_overseas_site_gets_a_sequential_IYC_ORS_reference()
    {
        var type = AddOverseasSiteType();
        var service = BuildService(type);

        var first = await SubmitAsync(service, type, new BsonDocument { ["accreditationYear"] = 2026 });
        var second = await SubmitAsync(service, type, new BsonDocument { ["accreditationYear"] = 2026 });

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        var firstStored = await GetAsync(first.WorkItem!.Id);
        var secondStored = await GetAsync(second.WorkItem!.Id);
        Assert.Equal("IYC-ORS-2026-00001", firstStored.Payload["applicationReference"].AsString);
        Assert.Equal("IYC-ORS-2026-00002", secondStored.Payload["applicationReference"].AsString);

        // The birth audit entry records the same server-generated reference.
        Assert.Equal(
            "IYC-ORS-2026-00001",
            Assert.Single(firstStored.AuditLog).Details["applicationReference"]
        );
    }

    [Fact]
    public async Task Submit_add_overseas_site_keeps_the_operator_supplied_paymentReference()
    {
        var type = AddOverseasSiteType();

        var result = await SubmitAsync(
            BuildService(type),
            type,
            new BsonDocument { ["accreditationYear"] = 2026, ["paymentReference"] = "PR/PK/REP/500500" }
        );

        var stored = await GetAsync(result.WorkItem!.Id);
        Assert.Equal("IYC-ORS-2026-00001", stored.Payload["applicationReference"].AsString);
        Assert.Equal("PR/PK/REP/500500", stored.Payload["paymentReference"].AsString);
    }

    [Fact]
    public async Task Submit_add_overseas_site_restarts_the_sequence_for_each_accreditation_year()
    {
        var type = AddOverseasSiteType();
        var service = BuildService(type);

        await SubmitAsync(service, type, new BsonDocument { ["accreditationYear"] = 2026 });
        await SubmitAsync(service, type, new BsonDocument { ["accreditationYear"] = 2026 });
        var nextYear = await SubmitAsync(service, type, new BsonDocument { ["accreditationYear"] = 2027 });

        var stored = await GetAsync(nextYear.WorkItem!.Id);
        Assert.Equal("IYC-ORS-2027-00001", stored.Payload["applicationReference"].AsString);
    }

    [Fact]
    public async Task Submit_add_overseas_site_without_accreditationYear_uses_the_current_utc_year()
    {
        var type = AddOverseasSiteType();

        var result = await SubmitAsync(BuildService(type), type, new BsonDocument());

        var stored = await GetAsync(result.WorkItem!.Id);
        Assert.Equal("IYC-ORS-2027-00001", stored.Payload["applicationReference"].AsString);
    }

    [Fact]
    public async Task Submit_other_types_keep_the_AP_reference_and_allocate_no_sequence()
    {
        var type = new TestWorkItemType("re-accreditation", "Re-accreditation");

        var result = await SubmitAsync(
            BuildService(type),
            type,
            new BsonDocument { ["accreditationYear"] = 2026 }
        );

        var stored = await GetAsync(result.WorkItem!.Id);
        Assert.Matches(@"^AP26EA$", stored.Payload["applicationReference"].AsString);
        Assert.Equal(
            0,
            await _clientFactory
                .GetCollection<BsonDocument>(MongoReferenceSequenceStore.CollectionName)
                .CountDocumentsAsync(
                    FilterDefinition<BsonDocument>.Empty,
                    cancellationToken: TestContext.Current.CancellationToken
                )
        );
    }

    [Fact]
    public async Task Submit_replay_returns_the_original_reference_without_allocating_a_new_number()
    {
        var type = AddOverseasSiteType();
        var service = BuildService(type);

        var first = await SubmitAsync(
            service,
            type,
            new BsonDocument { ["accreditationYear"] = 2026, ["operatorApplicationId"] = "change-001" }
        );
        var replay = await SubmitAsync(
            service,
            type,
            new BsonDocument { ["accreditationYear"] = 2026, ["operatorApplicationId"] = "change-001" }
        );

        Assert.False(first.IsIdempotentReplay);
        Assert.True(replay.IsIdempotentReplay);
        Assert.Equal(first.WorkItem!.Id, replay.WorkItem!.Id);
        Assert.Equal(
            "IYC-ORS-2026-00001",
            replay.WorkItem.Payload["applicationReference"].AsString
        );
        Assert.Equal(1L, await CounterAsync("IYC-ORS-2026"));
    }

    [Fact]
    public async Task Submit_reference_collision_takes_the_next_number_without_a_disambiguator()
    {
        // A reference already on file (for example inserted outside the
        // sequence) collides with the first number allocated. The retry takes
        // the next sequence number. It never gets the AP-style trailing
        // disambiguator character.
        var type = AddOverseasSiteType();
        var now = _time.GetUtcNow().UtcDateTime;
        await _persistence.CreateAsync(
            new WorkItem
            {
                TypeId = AddOverseasSiteTypeId,
                StateId = "submitted",
                SubmittedAt = now,
                LastModifiedAt = now,
                SubmittedBy = "seed",
                Payload = new BsonDocument { ["applicationReference"] = "IYC-ORS-2026-00001" },
            },
            TestContext.Current.CancellationToken
        );

        var result = await SubmitAsync(
            BuildService(type),
            type,
            new BsonDocument { ["accreditationYear"] = 2026 }
        );

        Assert.True(result.IsSuccess);
        var stored = await GetAsync(result.WorkItem!.Id);
        Assert.Equal("IYC-ORS-2026-00002", stored.Payload["applicationReference"].AsString);
        Assert.Equal(2L, await CounterAsync("IYC-ORS-2026"));
    }

    [Fact]
    public async Task Submit_concurrent_add_overseas_site_submissions_get_distinct_references()
    {
        const int Submissions = 20;
        var type = AddOverseasSiteType();
        var service = BuildService(type);

        var results = await Task.WhenAll(
            Enumerable
                .Range(0, Submissions)
                .Select(_ =>
                    SubmitAsync(service, type, new BsonDocument { ["accreditationYear"] = 2026 })
                )
        );

        var references = results
            .Select(r => r.WorkItem!.Payload["applicationReference"].AsString)
            .ToList();
        Assert.All(references, r => Assert.Matches(IycOrsPattern, r));
        Assert.Equal(
            Enumerable.Range(1, Submissions).Select(n => $"IYC-ORS-2026-{n:D5}"),
            references.Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public async Task Submit_add_overseas_site_without_a_configured_generator_throws_instead_of_falling_back_to_AP()
    {
        var type = AddOverseasSiteType();
        var service = new WorkItemService(
            new WorkItemRegistry([type]),
            _persistence,
            NullLogger<WorkItemService>.Instance,
            _time
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SubmitAsync(service, type, new BsonDocument { ["accreditationYear"] = 2026 })
        );
    }
}
