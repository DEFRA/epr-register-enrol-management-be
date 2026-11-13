using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-000: <see cref="ReAccreditationQueryService"/> against a real ephemeral
/// Mongo, so the version guard on <see cref="WorkItemPersistence.ReplaceAsync"/>
/// is the real one. The service writes the self-assign, the
/// <c>currentQuery</c> stamp and the query transition in one versioned save,
/// so of two overlapping queries the loser writes nothing and the query push
/// carries the winner's query. The freshness check matches query ids, so a
/// pod whose clock runs behind still pushes a genuine query.
/// </summary>
public sealed class ReAccreditationQueryRaceMongoIntegrationTests : IAsyncDisposable
{
    private const string ActionId = "query-during-duly-made";
    private static readonly string[] s_sections = ["business-plan"];
    private static readonly DateTime s_now = new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);

    private readonly TestMongoDbClientFactory _clientFactory;
    private readonly string _databaseName;
    private readonly WorkItemPersistence _persistence;
    private readonly IOperatorBackendPushAdapter _pushAdapter = Substitute.For<IOperatorBackendPushAdapter>();

    public ReAccreditationQueryRaceMongoIntegrationTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("reaccred-query-race");
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new WorkItemPersistence(_clientFactory, NullLoggerFactory.Instance);
        _pushAdapter
            .PushQueryRaisedAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(OperatorBackendPushResult.Success());
    }

    public async ValueTask DisposeAsync() =>
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

    private static ClaimsPrincipal User(string id, string name) =>
        new(new ClaimsIdentity(
            [new Claim("user:id", id), new Claim("user:name", name), new Claim("client_id", "test-client")],
            "test"));

    /// <summary>
    /// A duly-made item whose last transition was written at
    /// <paramref name="enteredDulyMadeAt"/>, with no accreditationId (which
    /// must stay absent, not become an explicit null: RA-291).
    /// </summary>
    private async Task<WorkItem> SeedDulyMadeAsync(DateTime enteredDulyMadeAt, CancellationToken ct)
    {
        var type = new ReAccreditationType();
        var item = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "duly-made",
            SubmittedAt = enteredDulyMadeAt.AddDays(-1),
            LastModifiedAt = enteredDulyMadeAt,
            SubmittedBy = "seed",
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion,
            Payload = new BsonDocument
            {
                ["applicationReference"] = "AP-RACE-" + Guid.NewGuid().ToString("N")[..8],
                ["organisationName"] = "Race Ltd",
            },
        };
        item.AuditLog.Add(FreshQueryTestData.ActionApplied("duly-make", "submitted", "duly-made", enteredDulyMadeAt));
        await _persistence.CreateAsync(item, ct);
        return item;
    }

    /// <summary>
    /// The engine and query service as one pod runs them, on the clock
    /// <paramref name="clock"/>, writing through <paramref name="persistence"/>.
    /// </summary>
    private ReAccreditationQueryService BuildService(IWorkItemPersistence persistence, TimeProvider clock)
    {
        var auditAppender = new WorkItemAuditAppender(
            persistence, NullLogger<WorkItemAuditAppender>.Instance, clock);
        var pushHook = new ReAccreditationQueryPushHook(
            _pushAdapter, auditAppender, NullLogger<ReAccreditationQueryPushHook>.Instance);
        var engine = new WorkItemService(
            new WorkItemRegistry([new ReAccreditationType()]),
            persistence,
            NullLogger<WorkItemService>.Instance,
            TestReferenceGenerators.ApplicationOnly(),
            clock,
            [pushHook]);
        return new ReAccreditationQueryService(
            persistence, engine, auditAppender, NullLogger<ReAccreditationQueryService>.Instance, clock);
    }

    /// <summary>
    /// Real persistence that holds the first two <c>ReplaceAsync</c> calls
    /// until both have arrived, so both requests have loaded the item at the
    /// same version before either saves: the race the version guard settles.
    /// </summary>
    private IWorkItemPersistence RacingPersistence()
    {
        var bothLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replaceCalls = 0;
        var racing = Substitute.For<IWorkItemPersistence>();
        racing
            .GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => _persistence.GetByIdAsync(ci.ArgAt<Guid>(0), ci.ArgAt<CancellationToken>(1)));
        racing
            .ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var call = Interlocked.Increment(ref replaceCalls);
                if (call <= 2)
                {
                    if (call == 2)
                    {
                        bothLoaded.TrySetResult();
                    }

                    await bothLoaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }

                await _persistence.ReplaceAsync(ci.ArgAt<WorkItem>(0), ci.ArgAt<CancellationToken>(1));
            });
        return racing;
    }

    [Fact]
    public async Task Of_two_overlapping_queries_the_loser_writes_nothing_and_the_winners_query_is_pushed()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedDulyMadeAsync(s_now.AddHours(-1), ct);
        var service = BuildService(RacingPersistence(), new FakeTimeProvider(s_now));

        var results = await Task.WhenAll(
            service.QueryAsync(seeded.Id, s_sections, "Alice's question", User("alice-1", "Alice"), ct),
            service.QueryAsync(seeded.Id, s_sections, "Bob's question", User("bob-1", "Bob"), ct));

        var winner = Assert.Single(results, r => r.IsSuccess);
        var loser = Assert.Single(results, r => !r.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, loser.FailureCode);
        var (winnerId, winnerReason) = winner.WorkItem!.AssignedToId == "alice-1"
            ? ("alice-1", "Alice's question")
            : ("bob-1", "Bob's question");

        var stored = (await _persistence.GetByIdAsync(seeded.Id, ct))!;
        Assert.Equal("queried", stored.StateId);
        Assert.Equal(winnerId, stored.AssignedToId);
        var currentQuery = stored.Payload["currentQuery"].AsBsonDocument;
        Assert.Equal(winnerReason, currentQuery["reason"].AsString);
        Assert.Equal(winnerId, currentQuery["raisedBy"].AsString);
        Assert.False(stored.Payload.Contains("accreditationId"));

        // Nothing of the loser's: one assignment, one transition, one query
        // detail, all for the winner's query.
        var assigned = Assert.Single(stored.AuditLog, e => e.Action == "assigned");
        Assert.Equal(winnerId, assigned.Details["assigneeId"]);
        var transition = Assert.Single(
            stored.AuditLog, e => e.Action == "action-applied" && e.Details["actionId"] == ActionId);
        Assert.Equal(currentQuery["queryId"].AsString, transition.Details["queryId"]);
        var detail = Assert.Single(stored.AuditLog, e => e.Action == ReAccreditationQueryService.AuditAction);
        Assert.Equal(winnerReason, detail.Details["reason"]);
        Assert.Equal(currentQuery["queryId"].AsString, detail.Details["queryId"]);

        await _pushAdapter.Received(1).PushQueryRaisedAsync(
            seeded.Id, Arg.Any<Guid>(), winnerReason,
            Arg.Is<IReadOnlyList<string>>(s => s.SequenceEqual(s_sections)), Arg.Any<CancellationToken>());
        Assert.Single(stored.AuditLog, e => e.Action == "query-push-sent");
        Assert.DoesNotContain(stored.AuditLog, e => e.Action == "query-push-failed");
    }

    [Fact]
    public async Task A_query_from_a_pod_whose_clock_runs_behind_is_still_pushed()
    {
        // The item entered duly-made on a pod whose clock was five minutes
        // ahead, so this pod stamps the query, and writes the transition,
        // "before" the item entered the state it is queried from. A time
        // window rejected this query; matching ids does not.
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedDulyMadeAsync(s_now, ct);
        var behind = new FakeTimeProvider(s_now.AddMinutes(-5));
        var service = BuildService(_persistence, behind);

        var result = await service.QueryAsync(
            seeded.Id, s_sections, "Tonnage does not reconcile", User("alice-1", "Alice"), ct);

        Assert.True(result.IsSuccess, result.Message);
        var stored = (await _persistence.GetByIdAsync(seeded.Id, ct))!;
        Assert.True(stored.Payload["currentQuery"]["raisedAt"].ToUniversalTime() < s_now);
        await _pushAdapter.Received(1).PushQueryRaisedAsync(
            seeded.Id, Arg.Any<Guid>(), "Tonnage does not reconcile",
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        Assert.Single(stored.AuditLog, e => e.Action == "query-push-sent");
        Assert.DoesNotContain(stored.AuditLog, e => e.Action == "query-push-failed");
    }
}
