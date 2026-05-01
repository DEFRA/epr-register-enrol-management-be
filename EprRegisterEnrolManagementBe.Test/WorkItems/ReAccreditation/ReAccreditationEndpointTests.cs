using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Endpoints;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// epr-19h: re-accreditation module endpoints exercised through the real
/// ASP.NET pipeline (auth handler, routing, validation, ProblemDetails)
/// against ephemeral MongoDB. The decision service stays substituted —
/// it is the module's collaborator under test, not an infrastructure
/// boundary the integration suite is supposed to hit.
/// </summary>
public class ReAccreditationEndpointTests
    : IClassFixture<MongoIntegrationFixture>
{
    private const string TenantClientId = "test-client";
    private const string CaseWorkerClientId = "case-worker-client";
    private const string DefaultUserId = "alice-1";
    private const string DefaultUserName = "Alice Example";

    private readonly MongoIntegrationFixture _fixture;

    public ReAccreditationEndpointTests(MongoIntegrationFixture fixture) => _fixture = fixture;

    // --------------------------- GetRecommendation ---------------------------

    [Fact]
    public async Task Recommendation_returns_not_found_when_work_item_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/work-items/re-accreditation/{Guid.NewGuid()}/recommendation", cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        factory.DecisionService.DidNotReceiveWithAnyArgs().EvaluateRecommendation(default!);
    }

    [Fact]
    public async Task Recommendation_returns_problem_when_work_item_is_wrong_type()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = "some-other-type",
            StateId = "submitted",
            SubmittedBy = TenantClientId
        }, cancellationToken);

        var response = await client.GetAsync(
            $"/work-items/re-accreditation/{id}/recommendation", cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Recommendation_deserialises_payload_and_returns_decision_service_result()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var payload = new BsonDocument
        {
            ["organisationName"] = "Acme Recycling Ltd",
            ["registrationNumber"] = "EX-12345",
            ["materialsHandled"] = new BsonArray(["plastic", "paper"]),
            ["previousAccreditationYear"] = 2024,
            ["complianceIssuesReported"] = 1
        };
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            SubmittedBy = TenantClientId,
            Payload = payload
        }, cancellationToken);

        ReAccreditationPayload? capturedPayload = null;
        factory.DecisionService
            .EvaluateRecommendation(Arg.Do<ReAccreditationPayload>(p => capturedPayload = p))
            .Returns(new ReAccreditationRecommendation(
                ReAccreditationRecommendation.Approve, "Looks good"));

        var response = await client.GetAsync(
            $"/work-items/re-accreditation/{id}/recommendation", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReAccreditationRecommendationResponse>(cancellationToken);
        Assert.NotNull(body);
        Assert.Equal(ReAccreditationRecommendation.Approve, body!.Recommendation);
        Assert.Equal("Looks good", body.Rationale);

        Assert.NotNull(capturedPayload);
        Assert.Equal("Acme Recycling Ltd", capturedPayload!.OrganisationName);
        Assert.Equal("EX-12345", capturedPayload.RegistrationNumber);
        Assert.Equal(new[] { "plastic", "paper" }, capturedPayload.MaterialsHandled);
        Assert.Equal(2024, capturedPayload.PreviousAccreditationYear);
        Assert.Equal(1, capturedPayload.ComplianceIssuesReported);
    }

    [Fact]
    public async Task Recommendation_passes_empty_payload_when_work_item_payload_is_empty()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            SubmittedBy = TenantClientId,
            Payload = new BsonDocument()
        }, cancellationToken);
        factory.DecisionService
            .EvaluateRecommendation(Arg.Any<ReAccreditationPayload>())
            .Returns(new ReAccreditationRecommendation(
                ReAccreditationRecommendation.MoreInfoNeeded, "Missing fields"));

        var response = await client.GetAsync(
            $"/work-items/re-accreditation/{id}/recommendation", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReAccreditationRecommendationResponse>(cancellationToken);
        Assert.Equal(ReAccreditationRecommendation.MoreInfoNeeded, body!.Recommendation);
        factory.DecisionService.Received(1).EvaluateRecommendation(Arg.Any<ReAccreditationPayload>());
    }

    // -------------------- RecordDecisionRationale (atomicity) --------------------

    [Fact]
    public async Task RecordDecisionRationale_persists_note_and_completion_in_a_single_write()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAwaitingDecision(id, TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision-rationale",
            new DecisionRationaleRequest("Approved on the basis of full compliance history."),
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.NotNull(persisted);
        var note = Assert.Single(persisted!.Notes);
        Assert.StartsWith("[decision-rationale] ", note.Text);
        Assert.Contains("record-decision-rationale",
            persisted.CompletedTaskIdsByState["awaiting-decision"]);
        // Atomicity: WorkItemPersistence.ReplaceAsync bumps Version by 1
        // per write — exactly one write happened.
        Assert.Equal(1, persisted.Version);
        Assert.Equal(2, persisted.AuditLog.Count);
        Assert.Contains(persisted.AuditLog, a => a.Action == "note-added"
            && a.CreatedBy == DefaultUserId);
        Assert.Contains(persisted.AuditLog, a => a.Action == "task-completed"
            && a.Details.GetValueOrDefault("taskId") == "record-decision-rationale");
    }

    [Fact]
    public async Task RecordDecisionRationale_concurrency_conflict_persists_neither_half()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        // Bump version on disk between the engine's load and replace so
        // the real optimistic-concurrency path fires (not a mocked throw).
        await using var factory = new ReAccreditationFactory(_fixture, raceWorkItemId: id);
        using var client = factory.CreateClient();

        await factory.SeedAsync(BuildAwaitingDecision(id, TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision-rationale",
            new DecisionRationaleRequest("Approved on the basis of full compliance history."),
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.NotNull(persisted);
        Assert.Empty(persisted!.Notes);
        Assert.False(persisted.CompletedTaskIdsByState.TryGetValue("awaiting-decision", out _));
        // The competing race writer bumps Version once; the engine's
        // failed write does not bump it again.
        Assert.Equal(1, persisted.Version);
    }

    [Fact]
    public async Task RecordDecisionRationale_short_rationale_is_rejected_before_any_engine_call()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        // Seed nothing — and assert nothing was created either, to prove
        // the validation gate fires before persistence is touched.
        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision-rationale",
            new DecisionRationaleRequest("nope"),
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Null(persisted);
    }

    [Fact]
    public async Task RecordDecisionRationale_returns_not_found_for_missing_work_item()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{Guid.NewGuid()}/decision-rationale",
            new DecisionRationaleRequest("Approved on the basis of full compliance history."),
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RecordDecisionRationale_rejects_wrong_work_item_type()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = "some-other-type",
            StateId = "submitted",
            SubmittedBy = TenantClientId
        }, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision-rationale",
            new DecisionRationaleRequest("Approved on the basis of full compliance history."),
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        // No mutation: still version 0, no notes, no completed tasks.
        Assert.Equal(0, persisted!.Version);
        Assert.Empty(persisted.Notes);
    }

    // -------------------- Cross-tenant gating (epr-946) --------------------

    [Fact]
    public async Task Recommendation_returns_not_found_for_cross_tenant_caller()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            // Owned by another tenant — caller is 'test-client'.
            SubmittedBy = "other-tenant"
        }, cancellationToken);

        var response = await client.GetAsync(
            $"/work-items/re-accreditation/{id}/recommendation", cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        factory.DecisionService.DidNotReceiveWithAnyArgs().EvaluateRecommendation(default!);
    }

    [Fact]
    public async Task Recommendation_allows_case_worker_to_see_other_tenants_item()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(
            _fixture,
            clientId: CaseWorkerClientId,
            userId: "worker-1",
            roles: [WorkItemEndpoints.CaseWorkerRole, ReAccreditationType.DecisionMakerRole]);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            SubmittedBy = "other-tenant",
            Payload = new BsonDocument()
        }, cancellationToken);
        factory.DecisionService
            .EvaluateRecommendation(Arg.Any<ReAccreditationPayload>())
            .Returns(new ReAccreditationRecommendation(
                ReAccreditationRecommendation.Approve, "ok"));

        var response = await client.GetAsync(
            $"/work-items/re-accreditation/{id}/recommendation", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RecordDecisionRationale_returns_not_found_for_cross_tenant_caller()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAwaitingDecision(id, "other-tenant"), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision-rationale",
            new DecisionRationaleRequest("Approved on the basis of full compliance history."),
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Hand-crafted POST against another tenant's item must not append
        // the note or complete the rationale task.
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(0, persisted!.Version);
        Assert.Empty(persisted.Notes);
    }

    [Fact]
    public async Task RecordDecisionRationale_allows_case_worker_against_other_tenants_item()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(
            _fixture,
            clientId: CaseWorkerClientId,
            userId: "worker-1",
            roles: [WorkItemEndpoints.CaseWorkerRole, ReAccreditationType.DecisionMakerRole]);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAwaitingDecision(id, "other-tenant"), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision-rationale",
            new DecisionRationaleRequest("Approved on the basis of full compliance history."),
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(1, persisted!.Version);
    }

    // -------------------- Segregation of duties (epr-jdv) --------------------

    [Fact]
    public async Task RecordDecisionRationale_returns_forbidden_for_non_decision_maker()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(
            _fixture,
            roles: []); // Same tenant, but no DecisionMakerRole.
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        // No seed — we expect fail-closed before persistence is touched.
        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision-rationale",
            new DecisionRationaleRequest("Approved on the basis of full compliance history."),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Null(persisted);
    }

    [Fact]
    public async Task RecordDecisionRationale_case_worker_without_decision_maker_role_is_forbidden()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // Cross-tenant access (CaseWorkerRole) must not bypass segregation
        // of duties — a case-worker who is not also a DecisionMaker is denied.
        await using var factory = new ReAccreditationFactory(
            _fixture,
            clientId: CaseWorkerClientId,
            userId: "worker-1",
            roles: [WorkItemEndpoints.CaseWorkerRole]);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAwaitingDecision(id, "other-tenant"), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision-rationale",
            new DecisionRationaleRequest("Approved on the basis of full compliance history."),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(0, persisted!.Version);
        Assert.Empty(persisted.Notes);
    }

    // ------------------------------ Helpers ------------------------------

    private static WorkItem BuildAwaitingDecision(Guid id, string submittedBy)
    {
        var type = new ReAccreditationType();
        return new WorkItem
        {
            Id = id,
            TypeId = ReAccreditationType.Id,
            StateId = "awaiting-decision",
            SubmittedBy = submittedBy,
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion
        };
    }

    /// <summary>
    /// Wraps real persistence and runs <paramref name="onBeforeReplace"/>
    /// just before delegating to <see cref="ReplaceAsync"/> for a chosen
    /// work item id. Lets the test race a competing writer between the
    /// engine's load and replace so the real optimistic-concurrency path
    /// fires (not a mocked throw).
    /// </summary>
    private sealed class RacingPersistence(IWorkItemPersistence inner, Guid raceId, Func<Task> onBeforeReplace)
        : IWorkItemPersistence
    {
        public Task CreateAsync(WorkItem workItem, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(workItem, cancellationToken);

        public Task<bool> CreateIfAbsentAsync(WorkItem workItem, CancellationToken cancellationToken = default) =>
            inner.CreateIfAbsentAsync(workItem, cancellationToken);

        public Task<WorkItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetByIdAsync(id, cancellationToken);

        public Task<WorkItemPage> QueryAsync(WorkItemQuery query, CancellationToken cancellationToken = default) =>
            inner.QueryAsync(query, cancellationToken);

        public async Task ReplaceAsync(WorkItem workItem, CancellationToken cancellationToken = default)
        {
            if (workItem.Id == raceId)
            {
                await onBeforeReplace();
            }
            await inner.ReplaceAsync(workItem, cancellationToken);
        }
    }

    private sealed class ReAccreditationFactory : WebApplicationFactory<Program>
    {
        private readonly MongoIntegrationFixture _fixture;
        private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("reaccred");
        private readonly string _clientId;
        private readonly string? _userId;
        private readonly string _userName;
        private readonly string[] _roles;
        private readonly Guid? _raceWorkItemId;

        public IReAccreditationDecisionService DecisionService { get; } =
            Substitute.For<IReAccreditationDecisionService>();

        public ReAccreditationFactory(
            MongoIntegrationFixture fixture,
            string clientId = TenantClientId,
            string? userId = DefaultUserId,
            string userName = DefaultUserName,
            string[]? roles = null,
            Guid? raceWorkItemId = null)
        {
            _fixture = fixture;
            _clientId = clientId;
            _userId = userId;
            _userName = userName;
            _roles = roles ?? new[] { ReAccreditationType.DecisionMakerRole };
            _raceWorkItemId = raceWorkItemId;
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
                services.RemoveAll<IReAccreditationDecisionService>();

                var clientFactory = new TestMongoDbClientFactory(_fixture.ConnectionString, _databaseName);
                services.AddSingleton<IMongoDbClientFactory>(clientFactory);

                services.AddSingleton<IWorkItemPersistence>(sp =>
                {
                    var real = new WorkItemPersistence(clientFactory, sp.GetRequiredService<ILoggerFactory>());
                    if (_raceWorkItemId is { } raceId)
                    {
                        return new RacingPersistence(real, raceId, async () =>
                        {
                            // Mutate the on-disk doc so the engine's
                            // version-conditional ReplaceAsync misses.
                            var current = await real.GetByIdAsync(raceId);
                            if (current is not null)
                            {
                                await real.ReplaceAsync(current);
                            }
                        });
                    }
                    return real;
                });

                services.AddSingleton(DecisionService);
            });
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", _clientId);
            if (_userId is not null)
            {
                client.DefaultRequestHeaders.Add("x-cdp-user-id", _userId);
                client.DefaultRequestHeaders.Add("x-cdp-user-name", _userName);
            }
            if (_roles.Length > 0)
            {
                client.DefaultRequestHeaders.Add("x-cdp-user-roles", string.Join(",", _roles));
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
