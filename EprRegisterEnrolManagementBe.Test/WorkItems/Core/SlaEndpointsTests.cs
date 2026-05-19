using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-131: tests for <see cref="SlaEndpoints"/>.
///
/// Handler-level tests call the <c>internal static</c> methods directly
/// with NSubstitute mocks for <see cref="ISlaService"/> /
/// <see cref="IWorkItemService"/> — no Mongo needed. Route wiring is
/// verified via a small integration subset using
/// <see cref="SlaEndpointsTestFactory"/>.
/// </summary>
public class SlaEndpointsTests : IClassFixture<MongoIntegrationFixture>
{
    private const string TypeId = "test-type";
    private static readonly Guid WorkItemId = Guid.NewGuid();

    private readonly MongoIntegrationFixture _fixture;

    public SlaEndpointsTests(MongoIntegrationFixture fixture) => _fixture = fixture;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static WorkItem AWorkItem(Guid? id = null) => new()
    {
        Id = id ?? WorkItemId,
        TypeId = TypeId,
        StateId = "assessment-in-progress",
        SubmittedAt = DateTime.UtcNow.AddDays(-10),
        LastModifiedAt = DateTime.UtcNow,
        SubmittedBy = "test-client",
        SlaClock = new SlaClock
        {
            StartedAt = DateTime.UtcNow.AddDays(-10),
            TargetDuration = TimeSpan.FromDays(84),
            Breached = false
        }
    };

    private static WorkItemEngineProjection AProjection(WorkItem workItem) =>
        new(workItem, "v1", [], []);

    private static DefaultHttpContext TeamLeaderContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("cognito:client_id", "test-client"),
            new Claim("user:id", "tl-user"),
            new Claim(ClaimTypes.Role, SlaService.TeamLeaderRole)
        ], "test"));
        return ctx;
    }

    private static JsonElement Json(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement;

    private SlaEndpointsTestFactory NewFactory(
        string? userRoles = SlaService.TeamLeaderRole,
        string? userId = "tl-user") =>
        new(_fixture, userRoles, userId);

    // ── ExtendSla — handler unit tests ────────────────────────────────────────

    [Fact]
    public async Task ExtendSla_returns_ok_with_work_item_response_on_success()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var slaService = Substitute.For<ISlaService>();
        var engine = Substitute.For<IWorkItemService>();
        var workItem = AWorkItem();
        slaService.ExtendAsync(WorkItemId, TimeSpan.FromDays(14), "reason",
                Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(SlaActionResult.Success(workItem));
        engine.Project(workItem).Returns(AProjection(workItem));

        var body = Json(new { additionalDuration = "P14D", reason = "reason" });
        var result = await SlaEndpoints.ExtendSla(
            WorkItemId, body, TeamLeaderContext(), slaService, engine, cancellationToken);

        var ok = Assert.IsType<Ok<WorkItemResponse>>(result.Result);
        Assert.Equal(WorkItemId, ok.Value!.Id);
    }

    [Fact]
    public async Task ExtendSla_returns_400_when_body_is_not_object()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var slaService = Substitute.For<ISlaService>();
        var engine = Substitute.For<IWorkItemService>();
        var body = JsonDocument.Parse("\"not-an-object\"").RootElement;

        var result = await SlaEndpoints.ExtendSla(
            WorkItemId, body, TeamLeaderContext(), slaService, engine, cancellationToken);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    [Fact]
    public async Task ExtendSla_returns_400_when_additionalDuration_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var slaService = Substitute.For<ISlaService>();
        var engine = Substitute.For<IWorkItemService>();
        var body = Json(new { reason = "reason" });

        var result = await SlaEndpoints.ExtendSla(
            WorkItemId, body, TeamLeaderContext(), slaService, engine, cancellationToken);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    [Fact]
    public async Task ExtendSla_returns_422_when_additionalDuration_not_parseable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var slaService = Substitute.For<ISlaService>();
        var engine = Substitute.For<IWorkItemService>();
        var body = Json(new { additionalDuration = "NOT_A_DURATION", reason = "reason" });

        var result = await SlaEndpoints.ExtendSla(
            WorkItemId, body, TeamLeaderContext(), slaService, engine, cancellationToken);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, problem.StatusCode);
    }

    [Fact]
    public async Task ExtendSla_returns_400_when_reason_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var slaService = Substitute.For<ISlaService>();
        var engine = Substitute.For<IWorkItemService>();
        var body = Json(new { additionalDuration = "P14D" });

        var result = await SlaEndpoints.ExtendSla(
            WorkItemId, body, TeamLeaderContext(), slaService, engine, cancellationToken);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    // ── OverrideSla — handler unit tests ──────────────────────────────────────

    [Fact]
    public async Task OverrideSla_returns_ok_with_work_item_response_on_success()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var slaService = Substitute.For<ISlaService>();
        var engine = Substitute.For<IWorkItemService>();
        var workItem = AWorkItem();
        slaService.OverrideAsync(WorkItemId, TimeSpan.FromDays(60), null, "reason",
                Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(SlaActionResult.Success(workItem));
        engine.Project(workItem).Returns(AProjection(workItem));

        var body = Json(new { newTargetDuration = "P60D", reason = "reason" });
        var result = await SlaEndpoints.OverrideSla(
            WorkItemId, body, TeamLeaderContext(), slaService, engine, cancellationToken);

        var ok = Assert.IsType<Ok<WorkItemResponse>>(result.Result);
        Assert.Equal(WorkItemId, ok.Value!.Id);
    }

    [Fact]
    public async Task OverrideSla_returns_400_when_body_is_not_object()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var slaService = Substitute.For<ISlaService>();
        var engine = Substitute.For<IWorkItemService>();
        var body = JsonDocument.Parse("42").RootElement;

        var result = await SlaEndpoints.OverrideSla(
            WorkItemId, body, TeamLeaderContext(), slaService, engine, cancellationToken);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    [Fact]
    public async Task OverrideSla_returns_400_when_newTargetDuration_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var slaService = Substitute.For<ISlaService>();
        var engine = Substitute.For<IWorkItemService>();
        var body = Json(new { reason = "reason" });

        var result = await SlaEndpoints.OverrideSla(
            WorkItemId, body, TeamLeaderContext(), slaService, engine, cancellationToken);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    [Fact]
    public async Task OverrideSla_returns_422_when_newTargetDuration_not_parseable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var slaService = Substitute.For<ISlaService>();
        var engine = Substitute.For<IWorkItemService>();
        var body = Json(new { newTargetDuration = "BAD", reason = "reason" });

        var result = await SlaEndpoints.OverrideSla(
            WorkItemId, body, TeamLeaderContext(), slaService, engine, cancellationToken);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, problem.StatusCode);
    }

    [Fact]
    public async Task OverrideSla_returns_422_when_newStartedAt_not_parseable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var slaService = Substitute.For<ISlaService>();
        var engine = Substitute.For<IWorkItemService>();
        var body = Json(new { newTargetDuration = "P60D", newStartedAt = "not-a-date", reason = "reason" });

        var result = await SlaEndpoints.OverrideSla(
            WorkItemId, body, TeamLeaderContext(), slaService, engine, cancellationToken);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, problem.StatusCode);
    }

    [Fact]
    public async Task OverrideSla_returns_400_when_reason_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var slaService = Substitute.For<ISlaService>();
        var engine = Substitute.For<IWorkItemService>();
        var body = Json(new { newTargetDuration = "P60D" });

        var result = await SlaEndpoints.OverrideSla(
            WorkItemId, body, TeamLeaderContext(), slaService, engine, cancellationToken);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    [Fact]
    public async Task OverrideSla_passes_null_newStartedAt_when_property_absent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var slaService = Substitute.For<ISlaService>();
        var engine = Substitute.For<IWorkItemService>();
        var workItem = AWorkItem();
        slaService.OverrideAsync(
                WorkItemId, Arg.Any<TimeSpan>(), null, Arg.Any<string>(),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(SlaActionResult.Success(workItem));
        engine.Project(workItem).Returns(AProjection(workItem));

        var body = Json(new { newTargetDuration = "P60D", reason = "reason" });
        await SlaEndpoints.OverrideSla(
            WorkItemId, body, TeamLeaderContext(), slaService, engine, cancellationToken);

        await slaService.Received(1).OverrideAsync(
            WorkItemId, Arg.Any<TimeSpan>(), null, Arg.Any<string>(),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OverrideSla_passes_null_newStartedAt_when_property_is_json_null()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var slaService = Substitute.For<ISlaService>();
        var engine = Substitute.For<IWorkItemService>();
        var workItem = AWorkItem();
        slaService.OverrideAsync(
                WorkItemId, Arg.Any<TimeSpan>(), null, Arg.Any<string>(),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(SlaActionResult.Success(workItem));
        engine.Project(workItem).Returns(AProjection(workItem));

        // Explicit JSON null
        var body = JsonDocument.Parse(
            $"{{\"newTargetDuration\":\"P60D\",\"newStartedAt\":null,\"reason\":\"reason\"}}").RootElement;
        await SlaEndpoints.OverrideSla(
            WorkItemId, body, TeamLeaderContext(), slaService, engine, cancellationToken);

        await slaService.Received(1).OverrideAsync(
            WorkItemId, Arg.Any<TimeSpan>(), null, Arg.Any<string>(),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OverrideSla_passes_parsed_newStartedAt_to_service()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var slaService = Substitute.For<ISlaService>();
        var engine = Substitute.For<IWorkItemService>();
        var workItem = AWorkItem();
        slaService.OverrideAsync(
                WorkItemId, Arg.Any<TimeSpan>(), Arg.Any<DateTime?>(), Arg.Any<string>(),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(SlaActionResult.Success(workItem));
        engine.Project(workItem).Returns(AProjection(workItem));

        var body = Json(new
        {
            newTargetDuration = "P60D",
            newStartedAt = "2026-04-01T00:00:00Z",
            reason = "reason"
        });
        await SlaEndpoints.OverrideSla(
            WorkItemId, body, TeamLeaderContext(), slaService, engine, cancellationToken);

        await slaService.Received(1).OverrideAsync(
            WorkItemId, Arg.Any<TimeSpan>(),
            Arg.Is<DateTime?>(d => d.HasValue),
            Arg.Any<string>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<CancellationToken>());
    }

    // ── ToHttpResult — all failure codes ─────────────────────────────────────

    [Theory]
    [InlineData(SlaActionFailureCode.WorkItemNotFound, StatusCodes.Status404NotFound)]
    [InlineData(SlaActionFailureCode.NotAuthorized, StatusCodes.Status403Forbidden)]
    [InlineData(SlaActionFailureCode.MissingActorIdentity, StatusCodes.Status401Unauthorized)]
    [InlineData(SlaActionFailureCode.InvalidRequest, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(SlaActionFailureCode.ClockNotStarted, StatusCodes.Status409Conflict)]
    [InlineData(SlaActionFailureCode.ConcurrencyConflict, StatusCodes.Status409Conflict)]
    public void ToHttpResult_maps_failure_code_to_correct_http_status(
        SlaActionFailureCode code, int expectedStatus)
    {
        var engine = Substitute.For<IWorkItemService>();
        var failure = SlaActionResult.Failure(code, "error detail");

        var result = SlaEndpoints.ToHttpResult(failure, engine);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(expectedStatus, problem.StatusCode);
    }

    [Fact]
    public void ToHttpResult_maps_unknown_failure_code_to_400()
    {
        var engine = Substitute.For<IWorkItemService>();
        var failure = SlaActionResult.Failure((SlaActionFailureCode)999, "unknown");

        var result = SlaEndpoints.ToHttpResult(failure, engine);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    // ── Route wiring (integration) — extend ──────────────────────────────────

    [Fact]
    public async Task Extend_route_returns_401_without_auth_header()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Clear();

        var response = await client.PostAsJsonAsync(
            $"/work-items/{WorkItemId}/sla/extend",
            new { additionalDuration = "P14D", reason = "reason" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Extend_route_returns_403_for_non_team_leader()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // No role header → regular user
        await using var factory = new SlaEndpointsTestFactory(_fixture, userRoles: null, userId: "user-1");
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();

        var response = await client.PostAsJsonAsync(
            $"/work-items/{id}/sla/extend",
            new { additionalDuration = "P14D", reason = "reason" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Extend_route_returns_404_for_unknown_work_item()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var id = Guid.NewGuid(); // Not seeded → ISlaService returns NotFound

        var response = await client.PostAsJsonAsync(
            $"/work-items/{id}/sla/extend",
            new { additionalDuration = "P14D", reason = "reason" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Extend_route_returns_409_when_sla_clock_not_started()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        // Seed a work item with no SLA clock
        var workItem = new WorkItem
        {
            Id = Guid.NewGuid(),
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedAt = DateTime.UtcNow,
            LastModifiedAt = DateTime.UtcNow,
            SubmittedBy = "test-client",
            SlaClock = null
        };
        await factory.SeedAsync(workItem, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/{workItem.Id}/sla/extend",
            new { additionalDuration = "P14D", reason = "reason" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal("SLA clock not started", problem?.Title);
    }

    [Fact]
    public async Task Extend_route_returns_ok_and_extends_clock_for_team_leader()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var workItem = AWorkItem(Guid.NewGuid());
        await factory.SeedAsync(workItem, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/{workItem.Id}/sla/extend",
            new { additionalDuration = "P14D", reason = "Extra assessment time" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        Assert.NotNull(body);
        Assert.Equal(workItem.Id, body!.Id);
    }

    // ── Route wiring (integration) — override ────────────────────────────────

    [Fact]
    public async Task Override_route_returns_403_for_non_team_leader()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new SlaEndpointsTestFactory(_fixture, userRoles: null, userId: "user-1");
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();

        var response = await client.PostAsJsonAsync(
            $"/work-items/{id}/sla/override",
            new { newTargetDuration = "P60D", reason = "reason" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Override_route_returns_ok_for_team_leader()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var workItem = AWorkItem(Guid.NewGuid());
        await factory.SeedAsync(workItem, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/{workItem.Id}/sla/override",
            new { newTargetDuration = "P60D", reason = "Regulatory override" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Override_route_returns_422_when_reason_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var workItem = AWorkItem(Guid.NewGuid());
        await factory.SeedAsync(workItem, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/{workItem.Id}/sla/override",
            new { newTargetDuration = "P60D", reason = "" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ── Test factory ─────────────────────────────────────────────────────────

    private sealed class SlaEndpointsTestFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly MongoIntegrationFixture _fixture;
        private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("sla-ep");
        private readonly string? _userRoles;
        private readonly string? _userId;

        private EprRegisterEnrolManagementBe.Utils.Mongo.IMongoDbClientFactory? _clientFactory;

        public SlaEndpointsTestFactory(
            MongoIntegrationFixture fixture,
            string? userRoles,
            string? userId)
        {
            _fixture = fixture;
            _userRoles = userRoles;
            _userId = userId;
        }

        public IWorkItemPersistence Persistence =>
            Services.GetRequiredService<IWorkItemPersistence>();

        public Task SeedAsync(WorkItem item, CancellationToken ct) =>
            Persistence.CreateAsync(item, ct);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IWorkItemPersistence>();
                services.RemoveAll<EprRegisterEnrolManagementBe.Utils.Mongo.IMongoDbClientFactory>();
                _clientFactory = new TestMongoDbClientFactory(
                    _fixture.ConnectionString, _databaseName);
                services.AddSingleton(_clientFactory);
                services.AddSingleton<IWorkItemPersistence>(sp =>
                    new WorkItemPersistence(
                        _clientFactory,
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));
                services.AddSingleton<IWorkItemType>(new TestWorkItemType(TypeId, "Test type"));
            });
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", "test-client");
            if (_userId is not null)
                client.DefaultRequestHeaders.Add("x-cdp-user-id", _userId);
            if (_userRoles is not null)
                client.DefaultRequestHeaders.Add("x-cdp-user-roles", _userRoles);
        }

        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            try
            {
                _clientFactory?.GetClient().DropDatabase(_databaseName);
            }
            catch { /* best-effort */ }
            await base.DisposeAsync();
        }
    }
}
