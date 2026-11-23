using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.Workflows;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-495: the shared Standard-workflow lifecycle routes
/// (<c>/work-items/{id}/duly-make|decision|confirm-contact</c>) driven through
/// the real ASP.NET pipeline against ephemeral MongoDB, for case types that
/// exist only in this test: one that opts in with a push target and a charge
/// source, one that opts in with no push target, and one that does not opt in.
/// The operator-backend push adapter is a fake. Re-accreditation's own routes
/// keep their own suite (<c>ReAccreditationEndpointTests</c>).
/// </summary>
public sealed class StandardLifecycleEndpointTests(MongoIntegrationFixture fixture)
{
    private const string TenantClientId = "test-client";
    private const int TestChargePence = 300000;

    private static readonly DateOnly s_paymentDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3);

    // ------------------------------- duly make -------------------------------

    [Fact]
    public async Task Duly_make_moves_an_opted_in_type_to_duly_made_with_the_sla_clock_on_the_payment_date()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new LifecycleFactory(fixture, PushAdapterReturning(OperatorBackendPushResult.Success()));
        using var client = factory.CreateClient();
        var id = await factory.SeedAsync(TestTypes.Pushed, "submitted", ct);

        var response = await PostDulyMakeAsync(client, id, s_paymentDate.ToString("yyyy-MM-dd"), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("duly-made", body.GetProperty("stateId").GetString());

        var persisted = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal("duly-made", persisted!.StateId);
        Assert.Equal(s_paymentDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), persisted.SlaClock!.StartedAt);
        Assert.Equal(TimeSpan.FromDays(84), persisted.SlaClock.TargetDuration);
        Assert.Equal(s_paymentDate.ToString("yyyy-MM-dd"), persisted.Payload["paymentDate"].AsString);
        var applied = Assert.Single(persisted.AuditLog, a => a.Action == "action-applied");
        Assert.Equal("duly-make", applied.Details["actionId"]);
        Assert.Equal("submitted", applied.Details["fromStateId"]);
        Assert.Contains(persisted.AuditLog, a => a.Action == "sla-clock-started");
    }

    [Fact]
    public async Task Duly_make_pushes_the_new_status_to_the_types_own_push_target_after_the_write()
    {
        var ct = TestContext.Current.CancellationToken;
        var pushAdapter = PushAdapterReturning(OperatorBackendPushResult.Success());
        var statusPushed = new TaskCompletionSource<OperatorJourneyStatusChange>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        pushAdapter
            .PushStatusChangedAsync(
                OperatorBackendStatusRoute.AccreditationApplication,
                OperatorBackendPushPipeline.BestEffort,
                Arg.Any<OperatorJourneyStatusChange>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call =>
            {
                statusPushed.TrySetResult(call.ArgAt<OperatorJourneyStatusChange>(2));
                return Task.FromResult(OperatorBackendPushResult.Success());
            });
        await using var factory = new LifecycleFactory(fixture, pushAdapter);
        using var client = factory.CreateClient();
        var id = await factory.SeedAsync(TestTypes.Pushed, "submitted", ct);

        var response = await PostDulyMakeAsync(client, id, s_paymentDate.ToString("yyyy-MM-dd"), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var change = await statusPushed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(id, change.WorkItemId);
        Assert.Equal("submitted", change.FromStateId);
        Assert.Equal("duly-made", change.ToStateId);
        Assert.Equal("Duly made", change.ToStateDisplayName);
        Assert.Equal("duly-make", change.ActionId);
        await WaitForAuditAsync(factory, id, "status-push-sent", ct);
    }

    [Fact]
    public async Task Duly_make_details_carry_the_charge_from_the_types_own_charge_source()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new LifecycleFactory(fixture, PushAdapterReturning(OperatorBackendPushResult.Success()));
        using var client = factory.CreateClient();
        var id = await factory.SeedAsync(TestTypes.Pushed, "submitted", ct);

        var response = await client.GetAsync($"/work-items/{id}/duly-make", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(TestChargePence, body.GetProperty("chargePence").GetInt32());
    }

    [Theory]
    [InlineData(null, "payment-date-required")]
    [InlineData("03/04/2026", "payment-date-invalid")]
    [InlineData("2999-01-01", "payment-date-in-future")]
    public async Task Duly_make_validates_the_payment_date_as_re_accreditation_does(
        string? paymentDate,
        string errorCode
    )
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new LifecycleFactory(fixture, PushAdapterReturning(OperatorBackendPushResult.Success()));
        using var client = factory.CreateClient();
        var id = await factory.SeedAsync(TestTypes.Pushed, "submitted", ct);

        var response = await PostDulyMakeAsync(client, id, paymentDate, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(errorCode, problem.GetProperty("errorCode").GetString());
        Assert.Equal("paymentDate", problem.GetProperty("field").GetString());
        Assert.Equal("submitted", (await factory.Persistence.GetByIdAsync(id, ct))!.StateId);
    }

    [Fact]
    public async Task Re_accreditation_is_reachable_on_the_shared_routes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new LifecycleFactory(fixture, PushAdapterReturning(OperatorBackendPushResult.Success()));
        using var client = factory.CreateClient();
        var id = await factory.SeedAsync(
            new ReAccreditationType(),
            "submitted",
            ct,
            new BsonDocument { ["applicationReference"] = "RA-123456789", ["chargeAmountPence"] = 327600 }
        );

        var details = await client.GetFromJsonAsync<JsonElement>($"/work-items/{id}/duly-make", ct);
        var response = await PostDulyMakeAsync(client, id, s_paymentDate.ToString("yyyy-MM-dd"), ct);

        Assert.Equal(327600, details.GetProperty("chargePence").GetInt32());
        // RA-495: re-accreditation is not submitted per site, so nothing is related.
        Assert.Empty(details.GetProperty("relatedWorkItems").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("duly-made", (await factory.Persistence.GetByIdAsync(id, ct))!.StateId);
    }

    // --------------------------- types not opted in ---------------------------

    [Theory]
    [InlineData("GET", "duly-make", "submitted")]
    [InlineData("POST", "duly-make", "submitted")]
    [InlineData("POST", "decision", "assessment-started")]
    [InlineData("POST", "confirm-contact", "approved-pending-contact")]
    public async Task A_type_that_has_not_opted_in_is_refused_with_nothing_changed(
        string method,
        string route,
        string stateId
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var pushAdapter = PushAdapterReturning(OperatorBackendPushResult.Success());
        await using var factory = new LifecycleFactory(fixture, pushAdapter);
        using var client = factory.CreateClient();
        var id = await factory.SeedAsync(TestTypes.NotOptedIn, stateId, ct);

        var response = method == "GET"
            ? await client.GetAsync($"/work-items/{id}/{route}", ct)
            : await client.PostAsJsonAsync(
                $"/work-items/{id}/{route}",
                new { paymentDate = s_paymentDate.ToString("yyyy-MM-dd"), outcome = "approved" },
                ct
            );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("wrong-work-item-type", problem.GetProperty("errorCode").GetString());
        var persisted = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal(stateId, persisted!.StateId);
        Assert.Empty(persisted.AuditLog);
        Assert.Empty(pushAdapter.ReceivedCalls());
    }

    [Fact]
    public async Task Re_accreditation_routes_still_refuse_another_opted_in_type()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new LifecycleFactory(fixture, PushAdapterReturning(OperatorBackendPushResult.Success()));
        using var client = factory.CreateClient();
        var id = await factory.SeedAsync(TestTypes.Pushed, "submitted", ct);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/duly-make",
            new { paymentDate = s_paymentDate.ToString("yyyy-MM-dd") },
            ct
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("submitted", (await factory.Persistence.GetByIdAsync(id, ct))!.StateId);
    }

    // ------------------------ determination and contact ------------------------

    [Fact]
    public async Task Approving_then_confirming_contact_grants_without_minting_a_number_or_sending_an_email()
    {
        var ct = TestContext.Current.CancellationToken;
        var pushAdapter = PushAdapterReturning(OperatorBackendPushResult.Success());
        var numberAdapter = Substitute.For<IAccreditationNumberAdapter>();
        var notifyClient = Substitute.For<INotifyClient>();
        await using var factory = new LifecycleFactory(fixture, pushAdapter, numberAdapter, notifyClient);
        using var client = factory.CreateClient();
        var id = await factory.SeedAsync(TestTypes.Pushed, "assessment-started", ct);

        var determination = await PostDecisionAsync(client, id, "approved", ct);

        Assert.Equal(HttpStatusCode.OK, determination.StatusCode);
        var pending = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal("approved-pending-contact", pending!.StateId);
        Assert.Contains(
            pending.AuditLog,
            a => a.Action == "determination-made" && a.Details["outcome"] == "approved"
        );
        // Step 1 tells the operator journey nothing.
        Assert.DoesNotContain(pushAdapter.ReceivedCalls(), IsDecisionPush);

        var confirmation = await PostConfirmContactAsync(client, id, ct);

        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
        var push = DecisionPushChange(Assert.Single(pushAdapter.ReceivedCalls(), IsDecisionPush));
        Assert.Equal(id, push.WorkItemId);
        Assert.Equal("approved-pending-contact", push.FromStateId);
        Assert.Equal("approved", push.ToStateId);
        Assert.Equal("Granted", push.ToStateDisplayName);
        Assert.Equal("confirm-contact-approved", push.ActionId);

        var granted = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal("approved", granted!.StateId);
        var actions = granted.AuditLog.Select(a => a.Action).ToList();
        Assert.True(
            actions.IndexOf("applicant-contacted") < actions.IndexOf("status-push-sent"),
            "status-push-sent is recorded once the outcome has landed"
        );
        Assert.Contains("sla-clock-stopped", actions);
        Assert.False(granted.Payload.Contains("accreditationId"));

        await numberAdapter.DidNotReceiveWithAnyArgs().GenerateOrUpdateAccreditationNumberAsync(default!, TestContext.Current.CancellationToken);
        Assert.Empty(notifyClient.ReceivedCalls());
    }

    [Fact]
    public async Task Refusing_then_confirming_contact_closes_the_case_as_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var pushAdapter = PushAdapterReturning(OperatorBackendPushResult.Success());
        await using var factory = new LifecycleFactory(fixture, pushAdapter);
        using var client = factory.CreateClient();
        var id = await factory.SeedAsync(TestTypes.Pushed, "assessment-in-progress", ct);

        var determination = await PostDecisionAsync(client, id, "rejected", ct);
        Assert.Equal(HttpStatusCode.OK, determination.StatusCode);
        Assert.Equal("rejected-pending-contact", (await factory.Persistence.GetByIdAsync(id, ct))!.StateId);

        var confirmation = await PostConfirmContactAsync(client, id, ct);

        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
        var push = DecisionPushChange(Assert.Single(pushAdapter.ReceivedCalls(), IsDecisionPush));
        Assert.Equal("rejected-pending-contact", push.FromStateId);
        Assert.Equal("rejected", push.ToStateId);
        var refused = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal("rejected", refused!.StateId);
        Assert.Contains(
            refused.AuditLog,
            a => a.Action == "applicant-contacted" && a.Details["outcome"] == "rejected"
        );

        // A closed case takes no other determination.
        var again = await PostDecisionAsync(client, id, "approved", ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Confirm_contact_commits_nothing_when_the_push_fails_and_a_retry_completes_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var pushAdapter = PushAdapterReturning(
            OperatorBackendPushResult.Failure("operator journey unreachable"),
            OperatorBackendPushResult.Success()
        );
        await using var factory = new LifecycleFactory(fixture, pushAdapter);
        using var client = factory.CreateClient();
        var id = await factory.SeedAsync(TestTypes.Pushed, "approved-pending-contact", ct);

        var failed = await PostConfirmContactAsync(client, id, ct);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        var untouched = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal("approved-pending-contact", untouched!.StateId);
        var pushFailed = Assert.Single(untouched.AuditLog);
        Assert.Equal("status-push-failed", pushFailed.Action);
        Assert.Equal("operator journey unreachable", pushFailed.Details["errorMessage"]);

        var retried = await PostConfirmContactAsync(client, id, ct);

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        var granted = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal("approved", granted!.StateId);
        Assert.Contains(granted.AuditLog, a => a.Action == "status-push-sent");
        Assert.Equal(2, pushAdapter.ReceivedCalls().Count(IsDecisionPush));
    }

    [Fact]
    public async Task A_type_with_no_push_target_goes_through_the_lifecycle_without_telling_the_operator_journey()
    {
        var ct = TestContext.Current.CancellationToken;
        var pushAdapter = PushAdapterReturning(OperatorBackendPushResult.Success());
        await using var factory = new LifecycleFactory(fixture, pushAdapter);
        using var client = factory.CreateClient();
        var id = await factory.SeedAsync(TestTypes.NoPush, "submitted", ct);

        Assert.Equal(
            HttpStatusCode.OK,
            (await PostDulyMakeAsync(client, id, s_paymentDate.ToString("yyyy-MM-dd"), ct)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsync($"/work-items/{id}/actions/start-assessment", null, ct)).StatusCode
        );
        Assert.Equal(HttpStatusCode.OK, (await PostDecisionAsync(client, id, "approved", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostConfirmContactAsync(client, id, ct)).StatusCode);

        var granted = await factory.Persistence.GetByIdAsync(id, ct);
        Assert.Equal("approved", granted!.StateId);
        Assert.DoesNotContain(granted.AuditLog, a => a.Action.StartsWith("status-push", StringComparison.Ordinal));
        Assert.Empty(pushAdapter.ReceivedCalls());
    }

    [Fact]
    public async Task A_repeated_confirmation_is_an_idempotent_replay()
    {
        var ct = TestContext.Current.CancellationToken;
        var pushAdapter = PushAdapterReturning(OperatorBackendPushResult.Success());
        await using var factory = new LifecycleFactory(fixture, pushAdapter);
        using var client = factory.CreateClient();
        var id = await factory.SeedAsync(TestTypes.Pushed, "approved-pending-contact", ct);
        Assert.Equal(HttpStatusCode.OK, (await PostConfirmContactAsync(client, id, ct)).StatusCode);

        var replay = await PostConfirmContactAsync(client, id, ct);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues(WorkItemEndpoints.IdempotentReplayHeader).Single());
        Assert.Single(pushAdapter.ReceivedCalls(), IsDecisionPush);
    }

    // -------------------------------- helpers --------------------------------

    private static Task<HttpResponseMessage> PostDulyMakeAsync(
        HttpClient client,
        Guid id,
        string? paymentDate,
        CancellationToken ct
    ) => client.PostAsJsonAsync($"/work-items/{id}/duly-make", new { paymentDate }, ct);

    private static Task<HttpResponseMessage> PostDecisionAsync(
        HttpClient client,
        Guid id,
        string outcome,
        CancellationToken ct
    ) => client.PostAsJsonAsync($"/work-items/{id}/decision", new { outcome }, ct);

    private static Task<HttpResponseMessage> PostConfirmContactAsync(HttpClient client, Guid id, CancellationToken ct) =>
        client.PostAsync($"/work-items/{id}/confirm-contact", content: null, ct);

    private static bool IsDecisionPush(NSubstitute.Core.ICall call) =>
        call.GetMethodInfo().Name == nameof(IOperatorBackendPushAdapter.PushStatusChangedAsync)
        && call.GetArguments()
            is [OperatorBackendStatusRoute.AccreditationApplication, OperatorBackendPushPipeline.Decision, ..];

    private static OperatorJourneyStatusChange DecisionPushChange(NSubstitute.Core.ICall call) =>
        (OperatorJourneyStatusChange)call.GetArguments()[2]!;

    /// <summary>
    /// The fake operator-backend push adapter: every decision push returns the
    /// next of <paramref name="decisionResults"/> (the last one repeats);
    /// status pushes succeed.
    /// </summary>
    private static IOperatorBackendPushAdapter PushAdapterReturning(params OperatorBackendPushResult[] decisionResults)
    {
        var adapter = Substitute.For<IOperatorBackendPushAdapter>();
        adapter
            .PushStatusChangedAsync(
                OperatorBackendStatusRoute.AccreditationApplication,
                OperatorBackendPushPipeline.Decision,
                Arg.Any<OperatorJourneyStatusChange>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                Task.FromResult(decisionResults[0]),
                decisionResults.Skip(1).Select(Task.FromResult).ToArray()
            );
        adapter
            .PushStatusChangedAsync(
                OperatorBackendStatusRoute.AccreditationApplication,
                OperatorBackendPushPipeline.BestEffort,
                Arg.Any<OperatorJourneyStatusChange>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(OperatorBackendPushResult.Success()));
        return adapter;
    }

    private static async Task WaitForAuditAsync(LifecycleFactory factory, Guid id, string action, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var item = await factory.Persistence.GetByIdAsync(id, ct);
            if (item!.AuditLog.Any(a => a.Action == action))
            {
                return;
            }
            await Task.Delay(50, ct);
        }
        Assert.Fail($"No '{action}' audit entry was written for work item {id}.");
    }

    // ------------------------------- test types -------------------------------

    /// <summary>A Standard-workflow case type that exists only in this test.</summary>
    private sealed class StandardTestType(string typeId) : IWorkItemType
    {
        public string TypeId => typeId;
        public string DisplayName => "Standard lifecycle test";
        public string TemplateVersion => "v1";
        public WorkItemWorkflow Workflow => StandardWorkflow.Definition;
        public WorkItemState InitialState => Workflow.InitialState;
        public IReadOnlyCollection<WorkItemState> States => Workflow.States;
        public IReadOnlyCollection<WorkItemTransition> Transitions => Workflow.Transitions;
    }

    private static class TestTypes
    {
        public static readonly IWorkItemType Pushed = new StandardTestType("test-standard-pushed");
        public static readonly IWorkItemType NoPush = new StandardTestType("test-standard-no-push");
        public static readonly IWorkItemType NotOptedIn = new StandardTestType("test-standard-not-opted-in");
    }

    /// <summary>
    /// The test type's push target: the fake adapter's accreditation-application
    /// route, best-effort for status pushes and the decision pipeline for decisions.
    /// </summary>
    private sealed class FakeAdapterPush : IOperatorJourneyPush
    {
        public Task<OperatorBackendPushResult> PushStatusChangedAsync(
            IOperatorBackendPushAdapter adapter,
            OperatorJourneyStatusChange change,
            CancellationToken cancellationToken
        ) =>
            adapter.PushStatusChangedAsync(
                OperatorBackendStatusRoute.AccreditationApplication,
                OperatorBackendPushPipeline.BestEffort,
                change,
                cancellationToken
            );

        public Task<OperatorBackendPushResult> PushDecisionAsync(
            IOperatorBackendPushAdapter adapter,
            OperatorJourneyStatusChange change,
            CancellationToken cancellationToken
        ) =>
            adapter.PushStatusChangedAsync(
                OperatorBackendStatusRoute.AccreditationApplication,
                OperatorBackendPushPipeline.Decision,
                change,
                cancellationToken
            );
    }

    private sealed class PushedTestStrategy(StandardDeterminationWrites determinations) : IStandardLifecycleStrategy
    {
        public IWorkItemType Type => TestTypes.Pushed;
        public IOperatorJourneyPush? OperatorJourneyPush { get; } = new FakeAdapterPush();
        public IStandardDeterminationSteps Determinations => determinations;

        public int? ReadChargePence(WorkItem workItem) =>
            workItem.Payload.TryGetValue("testChargePence", out var charge) ? charge.AsInt32 : null;
    }

    private sealed class NoPushTestStrategy(StandardDeterminationWrites determinations) : IStandardLifecycleStrategy
    {
        public IWorkItemType Type => TestTypes.NoPush;
        public IOperatorJourneyPush? OperatorJourneyPush => null;
        public IStandardDeterminationSteps Determinations => determinations;
        public int? ReadChargePence(WorkItem workItem) => null;
    }

    private sealed class LifecycleFactory(
        MongoIntegrationFixture fixture,
        IOperatorBackendPushAdapter pushAdapter,
        IAccreditationNumberAdapter? numberAdapter = null,
        INotifyClient? notifyClient = null
    ) : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("stdlife");

        public IWorkItemPersistence Persistence => Services.GetRequiredService<IWorkItemPersistence>();

        public async Task<Guid> SeedAsync(
            IWorkItemType type,
            string stateId,
            CancellationToken ct,
            BsonDocument? payload = null
        )
        {
            var id = Guid.NewGuid();
            await Persistence.CreateAsync(
                new WorkItem
                {
                    Id = id,
                    TypeId = type.TypeId,
                    StateId = stateId,
                    SubmittedBy = TenantClientId,
                    Payload = payload ?? new BsonDocument { ["testChargePence"] = TestChargePence },
                    TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
                    TemplateVersion = type.TemplateVersion,
                },
                ct
            );
            return id;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("WorkItems:SeedOnStartup", "false");
            if (notifyClient is not null)
            {
                // The notification hook is only registered when Notify is on.
                builder.UseSetting("Notify:Enabled", "true");
            }

            builder.ConfigureServices(services =>
            {
                services.UseEphemeralMongoPersistence(fixture, _databaseName);
                services.RemoveAll<IOperatorBackendPushAdapter>();
                services.AddSingleton(pushAdapter);
                services.RemoveAll<IAccreditationNumberAdapter>();
                services.AddSingleton(numberAdapter ?? Substitute.For<IAccreditationNumberAdapter>());
                if (notifyClient is not null)
                {
                    services.RemoveAll<INotifyClient>();
                    services.AddSingleton(notifyClient);
                }

                foreach (var type in new[] { TestTypes.Pushed, TestTypes.NoPush, TestTypes.NotOptedIn })
                {
                    services.AddSingleton(type);
                }
                services.AddStandardLifecycle<PushedTestStrategy>();
                services.AddStandardLifecycle<NoPushTestStrategy>();
            });
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, TenantClientId);
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
}
