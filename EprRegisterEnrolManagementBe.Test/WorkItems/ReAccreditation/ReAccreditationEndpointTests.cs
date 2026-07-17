using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Endpoints;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
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
            ["material"] = "plastic",
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
        Assert.Equal("plastic", capturedPayload.Material);
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

    // -------------------- Operator submission contract --------------------
    //
    // Guards the shape the real operator backend sends. The literal request
    // body below mirrors HttpCaseWorkingApiAdapter.BuildPayload in
    // epr-register-enrol-backend field-for-field (including the single
    // `material` string, not the legacy `materialsHandled` array) — if that
    // adapter's payload shape drifts from what this module deserialises into
    // ReAccreditationPayload, this test catches it here rather than silently
    // dropping fields on the case-mgmt side. Keep the two in sync.
    [Fact]
    public async Task Submit_persists_every_field_from_a_real_operator_submission_payload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var body = new
        {
            typeId = ReAccreditationType.Id,
            source = "operator-fe",
            payload = new
            {
                organisationName = "Acme Recycling Ltd",
                registrationNumber = "EPR-100023",
                material = "plastic",
                accreditationYear = 2026,
                previousAccreditationYear = 2025,
                complianceIssuesReported = 0,
                siteAddress = "123 High Street, London, SW1A 1AA",
                siteAddressPostcode = "SW1A 1AA",
                operatorApplicationId = "app-001",
                operatorOrganisationId = "12345",
                operatorRegistrationId = "reg-001",
                operatorEmail = "jane@example.com",
                submittedBy = new
                {
                    fullName = "Jane Smith",
                    jobTitle = "Operations Manager",
                    email = "jane@example.com"
                },
                prns = new
                {
                    plannedTonnageBand = "UpTo1000",
                    authorisers = new[]
                    {
                        new { fullName = "Bob Jones", email = "bob@example.com" }
                    }
                },
                businessPlan = new
                {
                    newInfrastructurePercent = 20,
                    priceSupportPercent = 20,
                    businessCollectionsPercent = 20,
                    communicationsPercent = 20,
                    newMarketsPercent = 10,
                    newUsesPercent = 10,
                    newInfrastructureDetail = "New sorting line",
                    priceSupportDetail = "Subsidised collection",
                    businessCollectionsDetail = "Kerbside expansion",
                    communicationsDetail = "Customer newsletter",
                    newMarketsDetail = "Export contracts",
                    newUsesDetail = "Recycled packaging"
                },
                samplingPlan = new
                {
                    files = new[]
                    {
                        new
                        {
                            filename = "sampling-plan.pdf",
                            uploadedAt = DateTime.UtcNow,
                            scanStatus = "Clean"
                        }
                    }
                }
            }
        };

        var response = await client.PostAsJsonAsync("/work-items", body, cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        var persisted = await factory.Persistence.GetByIdAsync(created!.Id, cancellationToken);
        Assert.NotNull(persisted);

        // Raw BSON check — this is what the case-mgmt frontend's work-items
        // list table and Application details page read directly.
        Assert.Equal("plastic", persisted!.Payload["material"].AsString);

        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(persisted.Payload);
        Assert.Equal("Acme Recycling Ltd", payload.OrganisationName);
        Assert.Equal("EPR-100023", payload.RegistrationNumber);
        Assert.Equal("plastic", payload.Material);
        Assert.Equal(2025, payload.PreviousAccreditationYear);
        Assert.Equal(0, payload.ComplianceIssuesReported);
        Assert.Equal("12345", payload.OperatorOrganisationId);
        Assert.Equal("reg-001", payload.OperatorRegistrationId);
        Assert.Equal("jane@example.com", payload.OperatorEmail);
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
            roles: [WorkItemEndpoints.CaseWorkerRole, "reaccreditation-decision-maker"]);
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
            roles: [WorkItemEndpoints.CaseWorkerRole, "reaccreditation-decision-maker"]);
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


    // -------------------- GetPriorYear endpoint --------------------

    [Fact]
    public async Task PriorYear_returns_not_found_when_work_item_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/work-items/re-accreditation/{Guid.NewGuid()}/prior-year", cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await factory.ReExClient.DidNotReceiveWithAnyArgs()
            .GetPriorYearAsync(default, default, default, default);
    }

    [Fact]
    public async Task PriorYear_returns_problem_when_work_item_is_wrong_type()
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
            $"/work-items/re-accreditation/{id}/prior-year", cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PriorYear_returns_not_found_when_reex_returns_null()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        factory.ReExClient
            .GetPriorYearAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PriorYearAccreditationDto?>(null));

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            SubmittedBy = TenantClientId,
            Payload = new BsonDocument
            {
                ["operatorOrganisationId"] = "org-42",
                ["operatorRegistrationId"] = "reg-99",
                ["previousAccreditationYear"] = 2024
            }
        }, cancellationToken);

        var response = await client.GetAsync(
            $"/work-items/re-accreditation/{id}/prior-year", cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PriorYear_returns_ok_with_prior_year_data_from_reex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var stubData = new PriorYearAccreditationDto
        {
            Year = 2024,
            TonnageBand = "UpTo1000",
            Authorisers = [new PriorYearAuthoriserDto { FullName = "Alice Smith", Email = "alice@example.com" }],
            BusinessPlan = new PriorYearBusinessPlanDto { NewInfrastructurePercent = 20 }
        };
        factory.ReExClient
            .GetPriorYearAsync("org-42", "reg-99", 2024, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PriorYearAccreditationDto?>(stubData));

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            SubmittedBy = TenantClientId,
            Payload = new BsonDocument
            {
                ["operatorOrganisationId"] = "org-42",
                ["operatorRegistrationId"] = "reg-99",
                ["previousAccreditationYear"] = 2024
            }
        }, cancellationToken);

        var response = await client.GetAsync(
            $"/work-items/re-accreditation/{id}/prior-year", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PriorYearAccreditationDto>(cancellationToken);
        Assert.NotNull(body);
        Assert.Equal(2024, body!.Year);
        Assert.Equal("UpTo1000", body.TonnageBand);
        Assert.Single(body.Authorisers);
        Assert.Equal("Alice Smith", body.Authorisers[0].FullName);
        Assert.Equal("alice@example.com", body.Authorisers[0].Email);
    }

    [Fact]
    public async Task PriorYear_passes_correct_identifiers_to_reex_client()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        string? capturedOrgId = null;
        string? capturedRegId = null;
        int? capturedYear = null;
        factory.ReExClient
            .GetPriorYearAsync(
                Arg.Do<string?>(v => capturedOrgId = v),
                Arg.Do<string?>(v => capturedRegId = v),
                Arg.Do<int?>(v => capturedYear = v),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PriorYearAccreditationDto?>(null));

        var id = Guid.NewGuid();
        await factory.SeedAsync(new WorkItem
        {
            Id = id,
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            SubmittedBy = TenantClientId,
            Payload = new BsonDocument
            {
                ["operatorOrganisationId"] = "org-77",
                ["operatorRegistrationId"] = "reg-88",
                ["previousAccreditationYear"] = 2023
            }
        }, cancellationToken);

        await client.GetAsync($"/work-items/re-accreditation/{id}/prior-year", cancellationToken);

        Assert.Equal("org-77", capturedOrgId);
        Assert.Equal("reg-88", capturedRegId);
        Assert.Equal(2023, capturedYear);
    }

    [Fact]
    public async Task PriorYear_returns_not_found_for_cross_tenant_caller()
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
            SubmittedBy = "other-tenant"
        }, cancellationToken);

        var response = await client.GetAsync(
            $"/work-items/re-accreditation/{id}/prior-year", cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await factory.ReExClient.DidNotReceiveWithAnyArgs()
            .GetPriorYearAsync(default, default, default, default);
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

    private static WorkItem BuildAssessmentInProgress(Guid id, string submittedBy)
    {
        var type = new ReAccreditationType();
        return new WorkItem
        {
            Id = id,
            TypeId = ReAccreditationType.Id,
            StateId = "assessment-in-progress",
            SubmittedBy = submittedBy,
            Payload = new BsonDocument
            {
                ["organisationName"] = "Acme Ltd",
                ["registrationNumber"] = "EX-001"
            },
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion
        };
    }

    // -------------------- RA-132: Approve endpoint --------------------

    [Fact]
    public async Task Approve_returns_ok_and_transitions_to_approved_for_decision_maker()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAwaitingDecision(id, TenantClientId), cancellationToken);

        var response = await client.PostAsync(
            $"/work-items/re-accreditation/{id}/approve", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal("approved", persisted!.StateId);
        // 1 for the approval ReplaceAsync, +1 for the queued publishing
        // audit, +1 for the notification hook's audit-sent entry.
        Assert.True(persisted.Version >= 1);
        var payload = MongoDB.Bson.Serialization.BsonSerializer
            .Deserialize<ReAccreditationPayload>(persisted.Payload);
        Assert.False(string.IsNullOrEmpty(payload.AccreditationId));
        Assert.NotNull(payload.AccreditationStartDate);
        Assert.NotNull(payload.SlaClock?.StoppedAt);
    }

    [Fact]
    public async Task Approve_returns_not_found_for_missing_work_item()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/work-items/re-accreditation/{Guid.NewGuid()}/approve", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Approve_returns_not_found_for_cross_tenant_caller()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAwaitingDecision(id, "other-tenant"), cancellationToken);

        var response = await client.PostAsync(
            $"/work-items/re-accreditation/{id}/approve", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("awaiting-decision", persisted!.StateId);
    }

    [Fact]
    public async Task Approve_succeeds_when_caller_holds_no_special_role()
    {
        // RA-323: every caseworker holds the same role, so approving
        // requires no role beyond being an authenticated caseworker in the
        // same tenant.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture, roles: []);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAwaitingDecision(id, TenantClientId), cancellationToken);

        var response = await client.PostAsync(
            $"/work-items/re-accreditation/{id}/approve", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("approved", persisted!.StateId);
    }

    [Fact]
    public async Task Approve_returns_bad_request_when_not_in_awaiting_decision()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAssessmentInProgress(id, TenantClientId), cancellationToken);

        var response = await client.PostAsync(
            $"/work-items/re-accreditation/{id}/approve", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

        public IReExAccreditationClient ReExClient { get; } =
            Substitute.For<IReExAccreditationClient>();

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
            _roles = roles ?? new[] { "reaccreditation-decision-maker" };
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
                services.RemoveAll<IReExAccreditationClient>();

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
                services.AddSingleton(ReExClient);
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
