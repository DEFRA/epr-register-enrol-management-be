using System.Security.Claims;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging;
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
/// production yet, so these tests use a test type that declares
/// <see cref="IWorkItemType.InYearChangeCode"/> <c>"ORS"</c>. That is the
/// contract the case-management story's type must follow.
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
        new(AddOverseasSiteTypeId, "Add overseas reprocessing site", inYearChangeCode: "ORS");

    private WorkItemService BuildService(IWorkItemType type, ILogger<WorkItemService>? logger = null) =>
        new(
            new WorkItemRegistry([type]),
            _persistence,
            logger ?? NullLogger<WorkItemService>.Instance,
            new WorkItemSubmissionServices(
                new WorkItemReferenceGenerator(
                    new ApplicationReferenceGenerator(_time),
                    new InYearChangeReferenceGenerator(_sequenceStore)
                ),
                TestOperatorBackendCaller.Default
            ),
            _time
        );

    private static Task<WorkItemActionResult> SubmitAsync(
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

    private static ClaimsPrincipal User(string clientId = "test-client") =>
        new(
            new ClaimsIdentity(
                [
                    new Claim("client_id", clientId),
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
        Assert.Equal("IYC-ORS-2026-00000", firstStored.Payload["applicationReference"].AsString);
        Assert.Equal("IYC-ORS-2026-00001", secondStored.Payload["applicationReference"].AsString);

        // The birth audit entry records the same server-generated reference.
        Assert.Equal(
            "IYC-ORS-2026-00000",
            Assert.Single(firstStored.AuditLog).Details["applicationReference"]
        );
    }

    // An in-year change request from the operator backend carries the org number
    // and the payment reference the operator backend built; both are stored exactly as sent.
    [Fact]
    public async Task Submit_add_overseas_site_from_the_operator_backend_keeps_its_paymentReference()
    {
        var type = AddOverseasSiteType();

        var result = await BuildService(type)
            .SubmitAsync(
                type,
                OperatorChangeRequestPayload(),
                TestOperatorBackendCaller.ClientId,
                User(TestOperatorBackendCaller.ClientId),
                cancellationToken: TestContext.Current.CancellationToken
            );

        Assert.True(result.IsSuccess);
        var stored = await GetAsync(result.WorkItem!.Id);
        Assert.Equal("IYC-ORS-2026-00000", stored.Payload["applicationReference"].AsString);
        Assert.Equal("PR/PK/REP/500500", stored.Payload["paymentReference"].AsString);
        Assert.Equal(500500, stored.Payload["operatorOrgNumber"].AsInt32);
    }

    private static BsonDocument OperatorChangeRequestPayload() =>
        new()
        {
            ["operatorRequestId"] = "req-1",
            ["operatorOrganisationId"] = "6a2fcd74e16883c137d01188",
            ["accreditationYear"] = 2026,
            ["operatorOrgNumber"] = 500500,
            ["paymentReference"] = "PR/PK/REP/500500",
        };

    private const string Absent = "<absent>";

    // Each invalid value, by label, so the theory rows stay serializable (xUnit1045).
    private static readonly Dictionary<string, BsonValue> s_invalidValues = new()
    {
        ["null"] = BsonNull.Value,
        ["string"] = "500500",
        ["zero"] = 0,
        ["negative"] = -1,
        ["above 999999"] = 1_000_000,
        ["fractional"] = 500500.5,
        ["empty"] = "",
        ["blank"] = "   ",
    };

    public static TheoryData<string, string, string> InvalidOperatorChangeRequests() =>
        new()
        {
            { "operatorOrgNumber", Absent, OperatorSubmissionRequirements.MissingOrgNumberMessage },
            { "operatorOrgNumber", "null", OperatorSubmissionRequirements.MissingOrgNumberMessage },
            { "operatorOrgNumber", "string", OperatorSubmissionRequirements.MissingOrgNumberMessage },
            { "operatorOrgNumber", "zero", OperatorSubmissionRequirements.MissingOrgNumberMessage },
            { "operatorOrgNumber", "negative", OperatorSubmissionRequirements.MissingOrgNumberMessage },
            { "operatorOrgNumber", "above 999999", OperatorSubmissionRequirements.MissingOrgNumberMessage },
            { "operatorOrgNumber", "fractional", OperatorSubmissionRequirements.MissingOrgNumberMessage },
            { "paymentReference", Absent, OperatorSubmissionRequirements.MissingPaymentReferenceMessage },
            { "paymentReference", "null", OperatorSubmissionRequirements.MissingPaymentReferenceMessage },
            { "paymentReference", "empty", OperatorSubmissionRequirements.MissingPaymentReferenceMessage },
            { "paymentReference", "blank", OperatorSubmissionRequirements.MissingPaymentReferenceMessage },
        };

    // An in-year change request from the operator backend without a valid org
    // number or payment reference is rejected before a sequence number is allocated, logs at
    // Error with the ids, and stores nothing.
    [Theory]
    [MemberData(nameof(InvalidOperatorChangeRequests))]
    public async Task Submit_add_overseas_site_from_the_operator_backend_without_a_valid_field_is_rejected(
        string field,
        string label,
        string expectedMessage
    )
    {
        var type = AddOverseasSiteType();
        var logger = new ListLogger<WorkItemService>();
        var payload = OperatorChangeRequestPayload();
        if (label == Absent)
        {
            payload.Remove(field);
        }
        else
        {
            payload[field] = s_invalidValues[label];
        }

        var result = await BuildService(type, logger)
            .SubmitAsync(
                type,
                payload,
                TestOperatorBackendCaller.ClientId,
                User(TestOperatorBackendCaller.ClientId),
                cancellationToken: TestContext.Current.CancellationToken
            );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidSubmissionPayload, result.FailureCode);
        Assert.Equal(expectedMessage, result.Message);
        Assert.Equal(0, await CountAsync("workItems"));
        Assert.Equal(0, await CountAsync(MongoReferenceSequenceStore.CollectionName));
        var error = Assert.Single(logger.Records, r => r.Level == LogLevel.Error);
        Assert.Equal(field, error.Properties["MissingFields"]);
        Assert.Equal(TestOperatorBackendCaller.ClientId, error.Properties["ClientId"]);
        Assert.Equal(AddOverseasSiteTypeId, error.Properties["TypeId"]);
        Assert.Equal("req-1", error.Properties["OperatorRequestId"]);
        Assert.Equal("6a2fcd74e16883c137d01188", error.Properties["OperatorOrganisationId"]);
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
        Assert.Equal("IYC-ORS-2027-00000", stored.Payload["applicationReference"].AsString);
    }

    public static TheoryData<BsonDocument> PayloadsWithoutAUsableYear =>
        new()
        {
            new BsonDocument(),
            new BsonDocument { ["accreditationYear"] = "2026" },
            new BsonDocument { ["accreditationYear"] = 26 },
            new BsonDocument { ["accreditationYear"] = 20260 },
            new BsonDocument { ["accreditationYear"] = 2026.5 },
        };

    [Theory]
    [MemberData(nameof(PayloadsWithoutAUsableYear))]
    public async Task Submit_add_overseas_site_without_a_usable_accreditationYear_is_rejected_and_allocates_nothing(
        BsonDocument payload
    )
    {
        var type = AddOverseasSiteType();

        var result = await SubmitAsync(BuildService(type), type, payload);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidSubmissionPayload, result.FailureCode);
        Assert.Contains("accreditationYear", result.Message);
        Assert.Equal(0, await CountAsync(MongoReferenceSequenceStore.CollectionName));
        Assert.Equal(0, await CountAsync("workItems"));
    }

    private async Task<long> CountAsync(string collection) =>
        await _clientFactory
            .GetCollection<BsonDocument>(collection)
            .CountDocumentsAsync(
                FilterDefinition<BsonDocument>.Empty,
                cancellationToken: TestContext.Current.CancellationToken
            );

    [Fact]
    public async Task Submit_other_types_keep_the_AP_reference_and_allocate_no_sequence()
    {
        var type = new TestWorkItemType("re-accreditation", "Re-accreditation");

        var result = await SubmitAsync(
            BuildService(type),
            type,
            new BsonDocument
            {
                ["accreditationYear"] = 2026,
                ["nation"] = "England",
                ["operatorOrgNumber"] = 500500,
                ["siteAddressPostcode"] = "SW1A 1AB",
                ["material"] = "Plastic",
            }
        );

        var stored = await GetAsync(result.WorkItem!.Id);
        Assert.Equal("AP26EA5005001ABPL", stored.Payload["applicationReference"].AsString);
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
            "IYC-ORS-2026-00000",
            replay.WorkItem.Payload["applicationReference"].AsString
        );
        Assert.Equal(1L, await CounterAsync("IYC-ORS-2026"));
    }

    [Fact]
    public async Task Submit_concurrent_replays_store_one_item_with_one_reference_and_may_skip_numbers()
    {
        // Concurrent submits with one operatorApplicationId can all miss the
        // up-front replay lookup. Each one allocates a number before its insert
        // hits the operatorApplicationId unique index. The losers then return
        // the winner as a replay, so their numbers are skipped (a documented
        // gap) but never reused.
        const int Submissions = 10;
        var type = AddOverseasSiteType();
        var service = BuildService(type);

        var results = await Task.WhenAll(
            Enumerable
                .Range(0, Submissions)
                .Select(_ =>
                    SubmitAsync(
                        service,
                        type,
                        new BsonDocument
                        {
                            ["accreditationYear"] = 2026,
                            ["operatorApplicationId"] = "change-race",
                        }
                    )
                )
        );

        Assert.All(results, r => Assert.True(r.IsSuccess));
        var stored = Assert.Single(
            (
                await _persistence.QueryAsync(
                    new WorkItemQuery(TypeIds: [AddOverseasSiteTypeId], Page: 1, PageSize: 50),
                    TestContext.Current.CancellationToken
                )
            ).Items
        );
        var reference = stored.Payload["applicationReference"].AsString;
        Assert.Matches(IycOrsPattern, reference);
        Assert.All(
            results,
            r =>
            {
                Assert.Equal(stored.Id, r.WorkItem!.Id);
                Assert.Equal(reference, r.WorkItem.Payload["applicationReference"].AsString);
            }
        );
        Assert.InRange(await CounterAsync("IYC-ORS-2026") ?? 0, 1L, Submissions);
    }

    // RA-492: the operator backend keys a change-request submission on
    // payload.operatorRequestId (it never sends operatorApplicationId for one),
    // so that is the field a retry after a timeout must replay on.
    [Fact]
    public async Task Submit_replay_by_operatorRequestId_returns_the_original_without_allocating()
    {
        var type = AddOverseasSiteType();
        var service = BuildService(type);

        var first = await SubmitAsync(
            service,
            type,
            new BsonDocument { ["accreditationYear"] = 2026, ["operatorRequestId"] = "request-001" }
        );
        var replay = await SubmitAsync(
            service,
            type,
            new BsonDocument { ["accreditationYear"] = 2026, ["operatorRequestId"] = "request-001" }
        );

        Assert.False(first.IsIdempotentReplay);
        Assert.True(replay.IsIdempotentReplay);
        Assert.Equal(first.WorkItem!.Id, replay.WorkItem!.Id);
        Assert.Equal("IYC-ORS-2026-00000", replay.WorkItem.Payload["applicationReference"].AsString);
        Assert.Equal(1L, await CounterAsync("IYC-ORS-2026"));
    }

    [Fact]
    public async Task Submit_replay_by_operatorRequestId_runs_before_payload_validation()
    {
        // A replay hands back the original even if the resubmitted payload
        // would now fail validation, and allocates nothing.
        var type = AddOverseasSiteType();
        var service = BuildService(type);
        var first = await SubmitAsync(
            service,
            type,
            new BsonDocument { ["accreditationYear"] = 2026, ["operatorRequestId"] = "request-002" }
        );

        var replay = await SubmitAsync(
            service,
            type,
            new BsonDocument { ["operatorRequestId"] = "request-002" }
        );

        Assert.True(replay.IsIdempotentReplay);
        Assert.Equal(first.WorkItem!.Id, replay.WorkItem!.Id);
        Assert.Equal(1L, await CounterAsync("IYC-ORS-2026"));
    }

    [Fact]
    public async Task Submit_concurrent_submits_with_one_operatorRequestId_store_one_item_with_one_reference()
    {
        const int Submissions = 10;
        var type = AddOverseasSiteType();
        var service = BuildService(type);

        var results = await Task.WhenAll(
            Enumerable
                .Range(0, Submissions)
                .Select(_ =>
                    SubmitAsync(
                        service,
                        type,
                        new BsonDocument
                        {
                            ["accreditationYear"] = 2026,
                            ["operatorRequestId"] = "request-race",
                        }
                    )
                )
        );

        Assert.All(results, r => Assert.True(r.IsSuccess));
        var stored = Assert.Single(
            (
                await _persistence.QueryAsync(
                    new WorkItemQuery(TypeIds: [AddOverseasSiteTypeId], Page: 1, PageSize: 50),
                    TestContext.Current.CancellationToken
                )
            ).Items
        );
        var reference = stored.Payload["applicationReference"].AsString;
        Assert.Matches(IycOrsPattern, reference);
        Assert.All(
            results,
            r =>
            {
                Assert.Equal(stored.Id, r.WorkItem!.Id);
                Assert.Equal(reference, r.WorkItem.Payload["applicationReference"].AsString);
            }
        );
        Assert.Single(results, r => !r.IsIdempotentReplay);
        Assert.InRange(await CounterAsync("IYC-ORS-2026") ?? 0, 1L, Submissions);
    }

    [Fact]
    public async Task Submit_different_operatorRequestIds_create_separate_work_items()
    {
        var type = AddOverseasSiteType();
        var service = BuildService(type);

        var first = await SubmitAsync(
            service,
            type,
            new BsonDocument { ["accreditationYear"] = 2026, ["operatorRequestId"] = "request-a" }
        );
        var second = await SubmitAsync(
            service,
            type,
            new BsonDocument { ["accreditationYear"] = 2026, ["operatorRequestId"] = "request-b" }
        );

        Assert.False(second.IsIdempotentReplay);
        Assert.NotEqual(first.WorkItem!.Id, second.WorkItem!.Id);
        Assert.Equal("IYC-ORS-2026-00001", second.WorkItem.Payload["applicationReference"].AsString);
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
                Payload = new BsonDocument { ["applicationReference"] = "IYC-ORS-2026-00000" },
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
        Assert.Equal("IYC-ORS-2026-00001", stored.Payload["applicationReference"].AsString);
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
            Enumerable.Range(0, Submissions).Select(n => $"IYC-ORS-2026-{n:D5}"),
            references.Order(StringComparer.Ordinal)
        );
    }
}
