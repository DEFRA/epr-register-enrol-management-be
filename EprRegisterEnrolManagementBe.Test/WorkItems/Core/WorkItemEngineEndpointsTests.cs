using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using EprRegisterEnrolManagementBe.Auth;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// epr-efp: persistence is real <see cref="WorkItemPersistence"/>
/// against ephemeral MongoDB. Tests seed via the exposed
/// <see cref="EngineFactory.Persistence"/> and re-read for assertions.
/// </summary>
public class WorkItemEngineEndpointsTests
{
    private const string TypeId = "test-type";
    private readonly MongoIntegrationFixture _fixture;

    public WorkItemEngineEndpointsTests(MongoIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Action_returns_200_when_no_tasks_gate_the_transition()
    {
        // RA-410: this used to assert 409 Conflict because the "approve"
        // transition was gated on an outstanding task. The task framework
        // (and the gate) are gone, so the same seed now simply succeeds —
        // regression cover for the ungating.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new EngineFactory(_fixture);
        using var client = factory.CreateClient();

        var workItemId = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = workItemId,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        }, cancellationToken);

        var response = await client.PostAsync(
            $"/work-items/{workItemId}/actions/approve", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        Assert.Equal("approved", body?.StateId);
    }

    [Fact]
    public async Task Action_transitions_state_when_tasks_complete()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new EngineFactory(_fixture);
        using var client = factory.CreateClient();

        var workItemId = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = workItemId,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        }, cancellationToken);

        var response = await client.PostAsync(
            $"/work-items/{workItemId}/actions/approve", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        Assert.Equal("approved", body?.StateId);
        Assert.Empty(body!.AvailableActions);
    }

    [Fact]
    public async Task Action_returns_404_when_work_item_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new EngineFactory(_fixture);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/work-items/{Guid.NewGuid()}/actions/approve", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Action_returns_400_when_action_unknown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new EngineFactory(_fixture);
        using var client = factory.CreateClient();

        var workItemId = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = workItemId,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        }, cancellationToken);

        var response = await client.PostAsync(
            $"/work-items/{workItemId}/actions/teleport", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_by_id_projects_engine_state_in_response()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new EngineFactory(_fixture);
        using var client = factory.CreateClient();

        var workItemId = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = workItemId,
            TypeId = TypeId,
            StateId = "submitted",
            SubmittedBy = "test-client"
        }, cancellationToken);

        var body = await client.GetFromJsonAsync<WorkItemResponse>($"/work-items/{workItemId}", cancellationToken);

        Assert.NotNull(body);
        // RA-410: "approve" used to be absent here because it was gated on
        // an outstanding task. The task framework (and the gate) are gone,
        // so the same seed now offers it — regression cover for the
        // ungating.
        Assert.Contains(body!.AvailableActions, a => a.ActionId == "approve");
    }

    private sealed class EngineFactory : WebApplicationFactory<Program>
    {
        private readonly MongoIntegrationFixture _fixture;
        private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("engine");

        public EngineFactory(MongoIntegrationFixture fixture) => _fixture = fixture;

        public IWorkItemPersistence Persistence => Services.GetRequiredService<IWorkItemPersistence>();

        public Task SeedAsync(WorkItem item, CancellationToken cancellationToken) =>
            Persistence.CreateAsync(item, cancellationToken);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IWorkItemPersistence>();
                services.RemoveAll<IMongoDbClientFactory>();
                var clientFactory = new TestMongoDbClientFactory(_fixture.ConnectionString, _databaseName);
                services.AddSingleton<IMongoDbClientFactory>(clientFactory);
                services.AddSingleton<IWorkItemPersistence>(sp =>
                    new WorkItemPersistence(clientFactory, sp.GetRequiredService<ILoggerFactory>()));

                services.AddSingleton<IWorkItemType>(new TestWorkItemType(
                    TypeId,
                    "Test type",
                    states:
                    [
                        new WorkItemState("submitted", "Submitted"),
                        new WorkItemState("approved", "Approved", IsTerminal: true)
                    ],
                    transitions:
                    [
                        new WorkItemTransition("approve", "Approve", "submitted", "approved")
                    ]));
            });
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, "test-client");
            client.DefaultRequestHeaders.Add("x-cdp-user-id", "test-user");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    var clientFactory = Services.GetRequiredService<IMongoDbClientFactory>();
                    clientFactory.GetClient().DropDatabase(_databaseName);
                }
                catch
                {
                    // Best-effort — the ephemeral instance dies with the fixture.
                }
            }
            base.Dispose(disposing);
        }
    }
}
