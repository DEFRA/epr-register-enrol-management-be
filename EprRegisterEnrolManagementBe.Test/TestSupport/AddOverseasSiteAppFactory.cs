using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.TestSupport;

/// <summary>
/// RA-495: the app with the production modules registered, against ephemeral
/// MongoDB, for the add-ORS lifecycle endpoint tests. Work items come from a
/// submission posted to <c>POST /work-items</c> as the operator backend posts
/// it. The operator-backend push adapter, the accreditation number adapter and
/// the Notify client are fakes: a test reads the pushes the adapter received
/// (every push succeeds unless told otherwise) and can assert the others are
/// never used. <see cref="ConfigureTestServices"/> adds a test's own
/// registrations (fault injection) after all of that.
/// </summary>
internal sealed class AddOverseasSiteAppFactory : WebApplicationFactory<Program>
{
    public const int SiteChargePence = 32800;

    /// <summary>The payment date the lifecycle tests duly make with: three days ago.</summary>
    public static readonly DateOnly PaymentDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3);

    private readonly MongoIntegrationFixture _fixture;

    private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("addors");

    /// <summary>The ephemeral database this app writes to.</summary>
    public string DatabaseName => _databaseName;

    /// <summary>Runs after the factory's own service registrations.</summary>
    public Action<IServiceCollection>? ConfigureTestServices { get; init; }

    /// <summary>Configuration settings (RA-769: <c>TestOrganisations:Enabled</c>) the app starts with.</summary>
    public IReadOnlyDictionary<string, string>? Settings { get; init; }

    public IOperatorBackendPushAdapter PushAdapter { get; } = Substitute.For<IOperatorBackendPushAdapter>();

    public IAccreditationNumberAdapter NumberAdapter { get; } = Substitute.For<IAccreditationNumberAdapter>();

    public INotifyClient NotifyClient { get; } = Substitute.For<INotifyClient>();

    public IWorkItemPersistence Persistence => Services.GetRequiredService<IWorkItemPersistence>();

    /// <summary>Posts an add-ORS submission and returns its work item ids, in site order.</summary>
    public async Task<List<Guid>> SubmitAsync(
        HttpClient client,
        string operatorRequestId,
        int siteCount,
        CancellationToken ct
    )
    {
        var response = await client.PostAsJsonAsync(
            "/work-items",
            AddOverseasSiteSubmissions.Body(operatorRequestId, siteCount: siteCount, siteChargePence: SiteChargePence),
            ct
        );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return body.GetProperty("workItems").EnumerateArray().Select(w => w.GetProperty("id").GetGuid()).ToList();
    }

    /// <summary>How many documents the app's database holds in <paramref name="collectionName"/>.</summary>
    public Task<long> CountAsync(string collectionName, CancellationToken ct) =>
        Services
            .GetRequiredService<IMongoDbClientFactory>()
            .GetCollection<BsonDocument>(collectionName)
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct);

    /// <summary>
    /// A one-site submission, duly made (its post-commit push delivered) and
    /// with its assessment started, as the front end leaves it before a
    /// determination.
    /// </summary>
    public async Task<Guid> DulyMadeAndStartedAsync(HttpClient client, string operatorRequestId, CancellationToken ct)
    {
        var id = (await SubmitAsync(client, operatorRequestId, siteCount: 1, ct))[0];
        Assert.Equal(HttpStatusCode.OK, (await PostDulyMakeAsync(client, id, ct)).StatusCode);
        // The duly-made push lands in the background; let it settle first.
        await WaitForAuditAsync(id, "status-push-sent", ct);
        Assert.Equal(HttpStatusCode.OK, (await PostStartAssessmentAsync(client, id, ct)).StatusCode);
        return id;
    }

    /// <summary><c>POST /work-items/{id}/duly-make</c> with <see cref="PaymentDate"/>.</summary>
    public static Task<HttpResponseMessage> PostDulyMakeAsync(HttpClient client, Guid id, CancellationToken ct) =>
        PostDulyMakeAsync(client, id, PaymentDate.ToString("yyyy-MM-dd"), ct);

    /// <summary><c>POST /work-items/{id}/duly-make</c> with the payment date as sent.</summary>
    public static Task<HttpResponseMessage> PostDulyMakeAsync(
        HttpClient client,
        Guid id,
        string? paymentDate,
        CancellationToken ct
    ) => client.PostAsJsonAsync($"/work-items/{id}/duly-make", new { paymentDate }, ct);

    /// <summary><c>POST /work-items/{id}/actions/start-assessment</c>.</summary>
    public static Task<HttpResponseMessage> PostStartAssessmentAsync(HttpClient client, Guid id, CancellationToken ct) =>
        client.PostAsync($"/work-items/{id}/actions/start-assessment", content: null, ct);

    /// <summary>
    /// RA-495 AC6 / RA-795: the effective date the helpers approve with. A fixed
    /// date inside the submissions' default accreditation year (2026), so it never
    /// depends on when the suite runs.
    /// </summary>
    public static readonly DateOnly EffectiveDate = new(2026, 6, 15);

    /// <summary>
    /// <c>POST /work-items/{id}/decision</c> with <paramref name="outcome"/>; an
    /// approval also sends <see cref="EffectiveDate"/>, as add-ORS requires (a
    /// refusal records the submission date instead).
    /// </summary>
    public static Task<HttpResponseMessage> PostDecisionAsync(
        HttpClient client,
        Guid id,
        string outcome,
        CancellationToken ct
    ) =>
        PostDecisionAsync(
            client,
            id,
            outcome,
            outcome == "approved" ? EffectiveDate.ToString("yyyy-MM-dd") : null,
            ct
        );

    /// <summary><c>POST /work-items/{id}/decision</c> with the effective date as sent.</summary>
    public static Task<HttpResponseMessage> PostDecisionAsync(
        HttpClient client,
        Guid id,
        string outcome,
        string? effectiveDate,
        CancellationToken ct
    ) => client.PostAsJsonAsync($"/work-items/{id}/decision", new { outcome, effectiveDate }, ct);

    /// <summary><c>POST /work-items/{id}/confirm-contact</c>.</summary>
    public static Task<HttpResponseMessage> PostConfirmContactAsync(HttpClient client, Guid id, CancellationToken ct) =>
        client.PostAsync($"/work-items/{id}/confirm-contact", content: null, ct);

    public AddOverseasSiteAppFactory(MongoIntegrationFixture fixture, params OperatorBackendPushResult[] decisionResults)
    {
        _fixture = fixture;
        var results = decisionResults.Length == 0 ? [OperatorBackendPushResult.Success()] : decisionResults;
        // Every push succeeds, except that each decision push returns the next
        // of decisionResults (the last one repeats).
        PushAdapter
            .PushStatusChangedAsync(
                Arg.Any<OperatorBackendStatusRoute>(),
                Arg.Any<OperatorBackendPushPipeline>(),
                Arg.Any<OperatorJourneyStatusChange>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(OperatorBackendPushResult.Success()));
        PushAdapter
            .PushStatusChangedAsync(
                Arg.Is(OperatorBackendStatusRoute.ChangeRequest),
                Arg.Is(OperatorBackendPushPipeline.Decision),
                Arg.Any<OperatorJourneyStatusChange>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(results[0]), results.Skip(1).Select(Task.FromResult).ToArray());
    }

    /// <summary>
    /// One status push the fake adapter received: its route, retry pipeline, transition and
    /// effective date.
    /// </summary>
    public sealed record Push(
        OperatorBackendStatusRoute Route,
        OperatorBackendPushPipeline Pipeline,
        Guid WorkItemId,
        string FromStateId,
        string ToStateId,
        string ActionId,
        DateOnly? EffectiveDate = null
    );

    /// <summary>
    /// Every push the fake adapter has received, in order. Add-ORS only ever
    /// sends status pushes, so any other call fails the cast and the test.
    /// </summary>
    public IReadOnlyList<Push> Pushes =>
        PushAdapter
            .ReceivedCalls()
            .Select(call =>
            {
                var args = call.GetArguments();
                var change = (OperatorJourneyStatusChange)args[2]!;
                return new Push(
                    (OperatorBackendStatusRoute)args[0]!,
                    (OperatorBackendPushPipeline)args[1]!,
                    change.WorkItemId,
                    change.FromStateId,
                    change.ToStateId,
                    change.ActionId,
                    change.EffectiveDate
                );
            })
            .ToList();

    /// <summary>
    /// Waits for <paramref name="count"/> audit entries named
    /// <paramref name="action"/> on the work item: the post-commit status push
    /// runs in the background, so its audit entry lands after the response.
    /// </summary>
    public async Task<WorkItem> WaitForAuditAsync(Guid workItemId, string action, CancellationToken ct, int count = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var item = await Persistence.GetByIdAsync(workItemId, ct);
            if (item!.AuditLog.Count(a => a.Action == action) >= count)
            {
                return item;
            }
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Work item {workItemId} has no {count} '{action}' audit entries.");
            }
            await Task.Delay(50, ct);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("WorkItems:SeedOnStartup", "false");
        // On, so a decision email would be sent if add-ORS registered a hook.
        builder.UseSetting("Notify:Enabled", "true");
        foreach (var (key, value) in Settings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureServices(services =>
        {
            services.UseEphemeralMongoPersistence(_fixture, _databaseName);
            services.RemoveAll<IOperatorBackendPushAdapter>();
            services.AddSingleton(PushAdapter);
            services.RemoveAll<IAccreditationNumberAdapter>();
            services.AddSingleton(NumberAdapter);
            services.RemoveAll<INotifyClient>();
            services.AddSingleton(NotifyClient);
            ConfigureTestServices?.Invoke(services);
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, "test-client");
        client.DefaultRequestHeaders.Add("x-cdp-user-id", "alice-1");
        client.DefaultRequestHeaders.Add("x-cdp-user-name", "Alice Example");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                Services.GetRequiredService<IMongoDbClientFactory>().GetClient().DropDatabase(_databaseName);
            }
            catch
            {
                // Best-effort; the ephemeral mongod dies with the fixture.
            }
        }
        base.Dispose(disposing);
    }
}
