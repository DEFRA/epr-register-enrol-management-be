using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using TestWorkItemType = EprRegisterEnrolManagementBe.Test.WorkItems.Core.TestWorkItemType;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.AddOverseasSite;

/// <summary>
/// RA-739: an add-ORS submission is created whole or not at all, is never
/// duplicated, and is rejected if malformed. Driven through
/// <c>POST /work-items</c> against the fixture's replica-set MongoDB, so the
/// transaction is real.
/// </summary>
public sealed class AddOverseasSiteSubmissionIntegrityTests(MongoIntegrationFixture fixture)
{
    private const string ReplayHeader = "X-Idempotent-Replay";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Submission(string operatorRequestId, int siteCount = 3) =>
        new()
        {
            ["typeId"] = "add-overseas-site",
            ["source"] = "operator-fe",
            ["payload"] = new JsonObject
            {
                ["operatorRequestId"] = operatorRequestId,
                ["changeType"] = "add-overseas-site",
                ["operatorOrganisationId"] = "org-1",
                ["organisationName"] = "Acme Recycling Ltd",
                ["operatorOrgNumber"] = 500500,
                ["material"] = "Plastic",
                ["accreditationYear"] = 2026,
                ["paymentReference"] = "PR/PK/REP/500500",
                ["sites"] = new JsonArray(
                    Enumerable
                        .Range(1, siteCount)
                        .Select(n =>
                            (JsonNode)
                                new JsonObject
                                {
                                    ["siteId"] = $"site-{n}",
                                    ["orsId"] = $"ORS-{n:D3}",
                                    ["siteName"] = $"Overseas site {n}",
                                }
                        )
                        .ToArray()
                ),
            },
        };

    private static JsonObject Payload(JsonObject submission) => submission["payload"]!.AsObject();

    private static JsonObject Site(JsonObject submission, int index) =>
        Payload(submission)["sites"]![index]!.AsObject();

    [Fact]
    public async Task Resubmitting_the_same_operatorRequestId_returns_the_original_body_as_a_replay_and_creates_nothing()
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var first = await client.PostAsJsonAsync("/work-items", Submission("request-replay"), Ct);
        var firstBody = await first.Content.ReadAsStringAsync(Ct);

        var replay = await client.PostAsJsonAsync("/work-items", Submission("request-replay"), Ct);

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", Assert.Single(replay.Headers.GetValues(ReplayHeader)));
        Assert.False(first.Headers.Contains(ReplayHeader));
        Assert.Equal(firstBody, await replay.Content.ReadAsStringAsync(Ct));
        Assert.Equal(3, await factory.CountAsync("workItems", Ct));
        Assert.Equal(1, await factory.CountAsync("submissions", Ct));

        // The replay allocated no sequence numbers.
        var next = await PostCreatedAsync(client, Submission("request-next", siteCount: 1));
        Assert.Equal("SUB-2026-0002", next.GetProperty("submissionReference").GetString());
        Assert.Equal(["IYC-ORS-2026-00004"], ApplicationReferences(next));
    }

    /// <summary>
    /// RA-739 PR review: <c>operatorRequestId</c> is the caller's idempotency
    /// key and the replay lookup matches on it alone, across every type that
    /// submits per site. Reused for a different type it must not hand back the
    /// first submission as a success: the caller would read another request's
    /// references as its own and never learn that its submission was ignored.
    /// The second per-site type is registered by this test, so it doesn't
    /// depend on which per-site types production registers.
    /// </summary>
    [Fact]
    public async Task Reusing_an_operatorRequestId_for_a_different_type_is_refused_not_replayed()
    {
        var otherType = new TestWorkItemType(
            "add-overseas-site-other",
            "Another per-site type",
            inYearChangeCode: "OTH",
            splitsSubmissionPerSite: true
        );
        await using var factory = NewFactory(
            services => services.AddSingleton<IWorkItemType>(otherType)
        );
        using var client = factory.CreateClient();
        await PostCreatedAsync(client, Submission("request-shared"));

        var reused = Submission("request-shared");
        reused["typeId"] = "add-overseas-site-other";
        var response = await client.PostAsJsonAsync("/work-items", reused, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(response.Headers.Contains(ReplayHeader));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Contains(
            "already on file",
            problem.GetProperty("detail").GetString(),
            StringComparison.Ordinal
        );
        // Nothing was created for the refused submission.
        Assert.Equal(3, await factory.CountAsync("workItems", Ct));
        Assert.Equal(1, await factory.CountAsync("submissions", Ct));
    }

    [Fact]
    public async Task A_replay_with_a_different_payload_returns_the_stored_submission()
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var first = await PostCreatedAsync(client, Submission("request-edited"));

        var edited = Submission("request-edited", siteCount: 1);
        Payload(edited)["organisationName"] = "Renamed Ltd";
        var replay = await client.PostAsJsonAsync("/work-items", edited, Ct);

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.True(replay.Headers.Contains(ReplayHeader));
        var replayBody = await replay.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(first.GetRawText(), replayBody.GetRawText());
        Assert.Equal(3, await factory.CountAsync("workItems", Ct));
    }

    [Fact]
    public async Task Concurrent_identical_submissions_create_one_set_of_work_items_and_get_the_same_references()
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var responses = await Task.WhenAll(
            Enumerable
                .Range(0, 5)
                .Select(_ => client.PostAsJsonAsync("/work-items", Submission("request-race"), Ct))
        );

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync(Ct)));
        Assert.Single(bodies.Distinct());
        Assert.Single(responses, r => !r.Headers.Contains(ReplayHeader));
        Assert.Equal(3, await factory.CountAsync("workItems", Ct));
        Assert.Equal(1, await factory.CountAsync("submissions", Ct));
        var body = JsonDocument.Parse(bodies[0]).RootElement;
        Assert.Equal("SUB-2026-0001", body.GetProperty("submissionReference").GetString());
        Assert.Equal(
            ["IYC-ORS-2026-00001", "IYC-ORS-2026-00002", "IYC-ORS-2026-00003"],
            ApplicationReferences(body)
        );
    }

    [Fact]
    public async Task A_request_that_misses_the_stored_submission_until_its_write_collides_gets_it_back_as_a_replay()
    {
        // df7.14: the outer duplicate-key -> replay branch. A concurrent
        // request usually loses on a write conflict and its retried
        // transaction then finds the winner; here both replay checks before
        // the write miss the winner, so the loser's write collides on a
        // duplicate key and the winner is found only after it.
        SubmissionStoreMissingChecks? store = null;
        await using var factory = NewFactory(
            services =>
                Decorate<ISubmissionStore>(
                    services,
                    inner => store = new SubmissionStoreMissingChecks(inner)
                )
        );
        using var client = factory.CreateClient();
        var winner = await client.PostAsJsonAsync("/work-items", Submission("request-collides"), Ct);
        var winnerBody = await winner.Content.ReadAsStringAsync(Ct);

        store!.MissChecks = true;
        var loser = await client.PostAsJsonAsync("/work-items", Submission("request-collides"), Ct);

        Assert.Equal(HttpStatusCode.Created, loser.StatusCode);
        Assert.Equal("true", Assert.Single(loser.Headers.GetValues(ReplayHeader)));
        Assert.Equal(winnerBody, await loser.Content.ReadAsStringAsync(Ct));
        // Proves the replay came from the lookup after the collision: the
        // up-front and in-transaction checks were both missed.
        Assert.Equal(1, store.MissedUpFrontChecks);
        Assert.True(store.MissedSessionChecks >= 1);
        Assert.Equal(3, await factory.CountAsync("workItems", Ct));
        Assert.Equal(1, await factory.CountAsync("submissions", Ct));

        // The loser's aborted transaction used no sequence numbers.
        store.MissChecks = false;
        var next = await PostCreatedAsync(client, Submission("request-after-collision", siteCount: 1));
        Assert.Equal("SUB-2026-0002", next.GetProperty("submissionReference").GetString());
        Assert.Equal(["IYC-ORS-2026-00004"], ApplicationReferences(next));
    }

    [Fact]
    public async Task A_failure_after_some_work_items_are_written_leaves_nothing_behind()
    {
        // The second site's work item insert fails, after the first site's
        // work item and both sequence increments were written.
        await using var factory = NewFactory(FailSessionInsert(2));
        using var client = factory.CreateClient();

        var failed = await client.PostAsJsonAsync("/work-items", Submission("request-fails"), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(0, await factory.CountAsync("workItems", Ct));
        Assert.Equal(0, await factory.CountAsync("submissions", Ct));
        Assert.Equal(0, await factory.CountAsync(MongoReferenceSequenceStore.CollectionName, Ct));

        // No sequence number was used: the next submission starts from 1.
        var next = await PostCreatedAsync(client, Submission("request-after-failure", siteCount: 1));
        Assert.Equal("SUB-2026-0001", next.GetProperty("submissionReference").GetString());
        Assert.Equal(["IYC-ORS-2026-00001"], ApplicationReferences(next));
    }

    [Fact]
    public async Task A_failure_leaves_existing_sequence_counters_unchanged()
    {
        // Inserts 1-3 are the first submission's sites. The second
        // submission's second site (insert 5) fails, after both of its
        // increments were applied to the existing counters.
        await using var factory = NewFactory(FailSessionInsert(5));
        using var client = factory.CreateClient();
        await PostCreatedAsync(client, Submission("request-first"));

        var failed = await client.PostAsJsonAsync("/work-items", Submission("request-fails"), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(3, await factory.CountAsync("workItems", Ct));
        Assert.Equal(1, await factory.CountAsync("submissions", Ct));
        var next = await PostCreatedAsync(client, Submission("request-after-failure", siteCount: 1));
        Assert.Equal("SUB-2026-0002", next.GetProperty("submissionReference").GetString());
        Assert.Equal(["IYC-ORS-2026-00004"], ApplicationReferences(next));
    }

    public static TheoryData<string> MalformedSubmissions =>
        new(
            "no operatorRequestId",
            "no accreditationYear",
            "string accreditationYear",
            "two-digit accreditationYear",
            "no operatorOrganisationId",
            "no organisationName",
            "no material",
            "no paymentReference",
            "blank organisationName",
            "no sites",
            "empty sites",
            "site without siteId",
            "site without orsId",
            "site without siteName",
            "site that is not an object",
            "repeated siteId"
        );

    [Theory]
    [MemberData(nameof(MalformedSubmissions))]
    public async Task A_malformed_submission_is_rejected_with_400_and_creates_nothing(string malformation)
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var submission = Submission($"request-{Guid.NewGuid():N}");
        var payload = Payload(submission);
        switch (malformation)
        {
            case "no operatorRequestId": payload.Remove("operatorRequestId"); break;
            case "no accreditationYear": payload.Remove("accreditationYear"); break;
            case "string accreditationYear": payload["accreditationYear"] = "2026"; break;
            case "two-digit accreditationYear": payload["accreditationYear"] = 26; break;
            case "no operatorOrganisationId": payload.Remove("operatorOrganisationId"); break;
            case "no organisationName": payload.Remove("organisationName"); break;
            case "no material": payload.Remove("material"); break;
            case "no paymentReference": payload.Remove("paymentReference"); break;
            case "blank organisationName": payload["organisationName"] = " "; break;
            case "no sites": payload.Remove("sites"); break;
            case "empty sites": payload["sites"] = new JsonArray(); break;
            case "site without siteId": Site(submission, 1).Remove("siteId"); break;
            case "site without orsId": Site(submission, 1).Remove("orsId"); break;
            case "site without siteName": Site(submission, 1).Remove("siteName"); break;
            case "site that is not an object": payload["sites"]![1] = "site-2"; break;
            case "repeated siteId": Site(submission, 2)["siteId"] = "site-1"; break;
            default: throw new ArgumentOutOfRangeException(nameof(malformation), malformation, null);
        }

        var response = await client.PostAsJsonAsync("/work-items", submission, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await factory.CountAsync("workItems", Ct));
        Assert.Equal(0, await factory.CountAsync("submissions", Ct));
        Assert.Equal(0, await factory.CountAsync(MongoReferenceSequenceStore.CollectionName, Ct));
    }

    [Fact]
    public async Task A_numeric_siteId_from_the_operator_backend_is_accepted_and_echoed_as_a_number()
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var submission = Submission("request-numeric", siteCount: 2);
        Site(submission, 0)["siteId"] = 101;
        Site(submission, 1)["siteId"] = 102;

        var body = await PostCreatedAsync(client, submission);

        Assert.Equal(
            [101, 102],
            body.GetProperty("workItems").EnumerateArray().Select(w => w.GetProperty("siteId").GetInt32())
        );
    }

    [Fact]
    public async Task A_numeric_and_a_string_siteId_with_the_same_digits_are_different_sites()
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var submission = Submission("request-mixed", siteCount: 2);
        Site(submission, 0)["siteId"] = 7;
        Site(submission, 1)["siteId"] = "7";

        await PostCreatedAsync(client, submission);

        Assert.Equal(2, await factory.CountAsync("workItems", Ct));
    }

    private static async Task<JsonElement> PostCreatedAsync(HttpClient client, JsonObject submission)
    {
        var response = await client.PostAsJsonAsync("/work-items", submission, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static IEnumerable<string?> ApplicationReferences(JsonElement body) =>
        body.GetProperty("workItems")
            .EnumerateArray()
            .Select(w => w.GetProperty("applicationReference").GetString());

    private AddOverseasSiteAppFactory NewFactory(Action<IServiceCollection>? configureServices = null) =>
        new(fixture) { ConfigureTestServices = configureServices };

    private static Action<IServiceCollection> FailSessionInsert(int failOn) =>
        services =>
            Decorate<IWorkItemPersistence>(
                services,
                inner => new FailingSessionInsertPersistence(inner, failOn)
            );

    /// <summary>Replace the last registration of <typeparamref name="TService"/> with <paramref name="wrap"/> of it.</summary>
    private static void Decorate<TService>(IServiceCollection services, Func<TService, TService> wrap)
        where TService : class
    {
        var real = services.Last(d => d.ServiceType == typeof(TService));
        services.Remove(real);
        services.AddSingleton(sp =>
            wrap(
                (TService)(
                    real.ImplementationInstance
                    ?? real.ImplementationFactory?.Invoke(sp)
                    ?? ActivatorUtilities.CreateInstance(sp, real.ImplementationType!)
                )
            )
        );
    }

    /// <summary>
    /// Fault injection: the Nth work item insert made inside a transaction
    /// throws instead of writing. Every other call goes to the real persistence.
    /// </summary>
    private sealed class FailingSessionInsertPersistence(IWorkItemPersistence inner, int failOn)
        : ForwardingWorkItemPersistence(inner)
    {
        private int _sessionInserts;

        public override Task CreateAsync(
            IClientSessionHandle session,
            WorkItem workItem,
            CancellationToken cancellationToken = default
        ) =>
            Interlocked.Increment(ref _sessionInserts) == failOn
                ? Task.FromException(new InvalidOperationException("Injected failure"))
                : base.CreateAsync(session, workItem, cancellationToken);
    }

    /// <summary>
    /// While <see cref="MissChecks"/> is set, the next up-front replay check
    /// and every in-transaction one find nothing, as for a request racing the
    /// one that stored the submission. Later checks see what is on file.
    /// </summary>
    private sealed class SubmissionStoreMissingChecks(ISubmissionStore inner) : ISubmissionStore
    {
        public bool MissChecks { get; set; }

        public int MissedUpFrontChecks { get; private set; }

        public int MissedSessionChecks { get; private set; }

        public Task<SubmissionRecord?> FindAsync(
            string operatorRequestId,
            CancellationToken cancellationToken = default
        )
        {
            if (MissChecks && MissedUpFrontChecks == 0)
            {
                MissedUpFrontChecks++;
                return Task.FromResult<SubmissionRecord?>(null);
            }
            return inner.FindAsync(operatorRequestId, cancellationToken);
        }

        public Task<SubmissionRecord?> FindAsync(
            IClientSessionHandle session,
            string operatorRequestId,
            CancellationToken cancellationToken = default
        )
        {
            if (MissChecks)
            {
                MissedSessionChecks++;
                return Task.FromResult<SubmissionRecord?>(null);
            }
            return inner.FindAsync(session, operatorRequestId, cancellationToken);
        }

        public Task InsertAsync(
            IClientSessionHandle session,
            SubmissionRecord record,
            CancellationToken cancellationToken = default
        ) => inner.InsertAsync(session, record, cancellationToken);
    }
}
