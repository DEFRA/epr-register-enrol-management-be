using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// epr-dt2: negative-path coverage for the auth and assignment role
/// boundaries documented in AGENTS.md. Every test exercises the real
/// pipeline (CognitoClientIdAuthenticationHandler, routing,
/// ProblemDetails) against ephemeral MongoDB so it can also assert the
/// fail-closed property — that no audit entry is written and no on-disk
/// version is bumped when the request is denied.
/// </summary>
public class WorkItemAuthBoundaryTests
    : IClassFixture<MongoIntegrationFixture>
{
    private const string TypeId = "test-type";
    private const string TenantClientId = "test-client";
    private readonly MongoIntegrationFixture _fixture;

    public WorkItemAuthBoundaryTests(MongoIntegrationFixture fixture) => _fixture = fixture;

    // --------- (1) Mutations require a 'user:id' claim — fail closed ---------

    [Fact]
    public async Task Complete_task_returns_401_when_user_id_claim_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BoundaryFactory(_fixture, userId: null);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(NewSubmittedItem(id), cancellationToken);

        var response = await client.PostAsync(
            $"/work-items/{id}/tasks/check-eligibility/complete", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Atomicity: the engine's RequireActorIdentity gate fires before
        // any mutation, so the persisted document is unchanged.
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(0, persisted!.Version);
        Assert.Empty(persisted.AuditLog);
        Assert.False(persisted.CompletedTaskIdsByState.TryGetValue("submitted", out _));
    }

    [Fact]
    public async Task Complete_task_returns_401_when_user_id_header_is_whitespace()
    {
        // The handler ignores whitespace user-id headers; the engine then
        // sees no 'user:id' claim and refuses the mutation. This is the
        // "claim present-ish but resolves to null/empty" path
        // (CognitoClientIdAuthenticationHandler whitespace filter +
        // WorkItemService.ResolveActorUserId).
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BoundaryFactory(_fixture, userId: "   ");
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(NewSubmittedItem(id), cancellationToken);

        var response = await client.PostAsync(
            $"/work-items/{id}/tasks/check-eligibility/complete", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(0, persisted!.Version);
        Assert.Empty(persisted.AuditLog);
    }

    // ---------------- (2) Assign / Unassign — RA-323: any caseworker ----------------

    [Fact]
    public async Task Assign_succeeds_when_standard_user_targets_another_user()
    {
        // RA-323: every caseworker holds the same role, so a standard user
        // can assign a work item to anyone, not just claim it for themselves.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BoundaryFactory(_fixture, userId: "alice-1");
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(NewSubmittedItem(id), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/{id}/assign",
            new { assigneeId = "bob-2", assigneeName = "Bob" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("bob-2", persisted!.AssignedToId);
        Assert.Equal(1, persisted.Version);
        Assert.Single(persisted.AuditLog);
    }

    [Fact]
    public async Task Assign_succeeds_when_standard_user_reassigns_already_assigned_item()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BoundaryFactory(_fixture, userId: "alice-1");
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var seeded = NewSubmittedItem(id);
        seeded.AssignedToId = "bob-2";
        seeded.AssignedToName = "Bob";
        await factory.SeedAsync(seeded, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/{id}/assign",
            new { assigneeId = "alice-1", assigneeName = "Alice" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("alice-1", persisted!.AssignedToId);
        Assert.Equal(1, persisted.Version);
        Assert.Single(persisted.AuditLog);
    }

    [Fact]
    public async Task Unassign_succeeds_for_standard_user()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BoundaryFactory(_fixture, userId: "alice-1");
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var seeded = NewSubmittedItem(id);
        seeded.AssignedToId = "alice-1";
        seeded.AssignedToName = "Alice";
        await factory.SeedAsync(seeded, cancellationToken);

        var response = await client.PostAsync($"/work-items/{id}/unassign", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Null(persisted!.AssignedToId);
        Assert.Equal(1, persisted.Version);
        Assert.Single(persisted.AuditLog);
    }

    // Coverage for "RBAC lives in the frontend now" (no ownership gate on
    // GetById/list) lives in WorkItemEndpointsTests — this class stays
    // scoped to the two boundaries above that still exist: missing actor
    // identity, and the RA-323 assign/unassign permission model.

    // ---------------------------- Helpers ----------------------------

    private static WorkItem NewSubmittedItem(Guid id) => new()
    {
        Id = id,
        TypeId = TypeId,
        StateId = "submitted",
        SubmittedBy = TenantClientId
    };

    private sealed class BoundaryFactory : WebApplicationFactory<Program>
    {
        private readonly MongoIntegrationFixture _fixture;
        private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("authbnd");
        private readonly string? _userId;

        public BoundaryFactory(
            MongoIntegrationFixture fixture,
            string? userId)
        {
            _fixture = fixture;
            _userId = userId;
        }

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
                services.AddSingleton<IWorkItemType>(new TestWorkItemType(TypeId, "Test type"));
            });
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", TenantClientId);
            if (_userId is not null)
            {
                // Use TryAddWithoutValidation so the test can deliberately
                // send a whitespace user-id (which HttpClient would
                // otherwise normalise away) for the
                // ResolveActorUserId-returns-null path.
                client.DefaultRequestHeaders.TryAddWithoutValidation("x-cdp-user-id", _userId);
            }
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
                    // Best-effort.
                }
            }
            base.Dispose(disposing);
        }
    }
}
