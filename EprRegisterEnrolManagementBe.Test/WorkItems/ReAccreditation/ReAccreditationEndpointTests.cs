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

    // -------------------- Ownership no longer gates access --------------------
    // RBAC (who may act on whose items) now lives entirely in the frontend;
    // the backend applies whatever the (shared-secret authenticated) caller
    // asks for regardless of who submitted the item.

    [Fact]
    public async Task Recommendation_returns_ok_for_item_not_submitted_by_caller()
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
    public async Task RecordDecisionRationale_succeeds_for_item_not_submitted_by_caller()
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

    // ------------------------------ Helpers ------------------------------

    // ------------------------------ RA-291 Query ------------------------------

    private static readonly string[] s_querySections = ["business-plan", "prn-tonnage"];

    private const string DefaultQueryReason =
        "The tonnage figures do not reconcile with the sampling plan.";

    private static QueryApplicationRequest QueryBody(string? reason = DefaultQueryReason) =>
        new(s_querySections, reason);

    private static QueryApplicationRequest QueryBody(string[]? sections, string? reason) =>
        new(sections, reason);

    [Theory]
    [InlineData("submitted", "query-during-duly-making")]
    [InlineData("duly-made", "query-during-duly-made")]
    [InlineData("assessment-in-progress", "query-during-assessment")]
    [InlineData("awaiting-decision", "query-during-decision")]
    public async Task Query_moves_the_application_to_queried_and_records_the_query_detail(
        string stateId,
        string expectedActionId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, stateId, TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/query", QueryBody(), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal("queried", persisted!.StateId);

        // The framework engine recorded the transition ...
        Assert.Contains(persisted.AuditLog, a => a.Action == "action-applied"
            && a.Details.GetValueOrDefault("actionId") == expectedActionId
            && a.Details.GetValueOrDefault("fromStateId") == stateId
            && a.Details.GetValueOrDefault("toStateId") == "queried");

        // ... and the module recorded what was actually asked for (AC05).
        var queryEntry = Assert.Single(persisted.AuditLog,
            a => a.Action == ReAccreditationQueryService.AuditAction);
        Assert.Equal(expectedActionId, queryEntry.Details.GetValueOrDefault("actionId"));
        Assert.Equal("business-plan,prn-tonnage", queryEntry.Details.GetValueOrDefault("sections"));
        Assert.Equal(
            "The tonnage figures do not reconcile with the sampling plan.",
            queryEntry.Details.GetValueOrDefault("reason"));
        Assert.Equal(DefaultUserId, queryEntry.CreatedBy);
        Assert.Equal(DefaultUserName, queryEntry.CreatedByName);

        // RA-291: the open query is stamped on the payload so the Queried
        // email can carry the reason. It must be exactly the reason recorded
        // on the audit entry — same record, one source of truth.
        var currentQuery = persisted.Payload!["currentQuery"].AsBsonDocument;
        Assert.Equal(DefaultQueryReason, currentQuery["reason"].AsString);
        Assert.Equal(
            ["business-plan", "prn-tonnage"],
            currentQuery["sections"].AsBsonArray.Select(v => v.AsString));
        Assert.Equal(DefaultUserId, currentQuery["raisedBy"].AsString);

        // RA-291: the query page promises "the application will also be
        // assigned to you", so the query self-assigns.
        Assert.Equal(DefaultUserId, persisted.AssignedToId);
        Assert.Equal(DefaultUserName, persisted.AssignedToName);
        Assert.Contains(persisted.AuditLog, a => a.Action == "assigned");

        // The response body must already carry the query-detail entry, not
        // just the transition the engine wrote against its own copy.
        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        Assert.NotNull(body);
        Assert.Equal("queried", body!.StateId);
        Assert.NotNull(body.AuditLog);
        Assert.Contains(body.AuditLog!,
            a => a.Action == ReAccreditationQueryService.AuditAction);
    }

    [Theory]
    // An application awaiting a response cannot be queried again ...
    [InlineData("queried")]
    // ... nor can one whose outcome is already recorded.
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    public async Task Query_returns_conflict_when_the_state_has_no_query_transition(string stateId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, stateId, TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/query", QueryBody(), cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.NotNull(problem);
        Assert.Contains(stateId, problem!.Detail);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(stateId, persisted!.StateId);
        Assert.DoesNotContain(persisted.AuditLog,
            a => a.Action == ReAccreditationQueryService.AuditAction);
    }

    [Fact]
    public async Task Querying_two_different_applications_in_the_same_database_both_succeed()
    {
        // RA-291 regression. The stamp used to rewrite the whole payload,
        // round-tripping it through ReAccreditationPayload and materialising
        // `accreditationId: null` as an explicit field. payload.accreditationId
        // carries a unique + SPARSE index, and sparse excludes only documents
        // where the field is ABSENT — so the first query entered the index with
        // a null key and the second collided, 500ing with a duplicate-key
        // error. Worse, the assign had already landed, leaving the application
        // assigned but not queried.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(first, "submitted", TenantClientId), cancellationToken);
        await factory.SeedAsync(BuildInState(second, "duly-made", TenantClientId), cancellationToken);

        var firstResponse = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{first}/query", QueryBody(), cancellationToken);
        var secondResponse = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{second}/query", QueryBody(), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        foreach (var id in new[] { first, second })
        {
            var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
            Assert.Equal("queried", persisted!.StateId);
            Assert.Equal(DefaultQueryReason, persisted.Payload["currentQuery"]["reason"].AsString);
        }
    }

    [Fact]
    public async Task Query_does_not_materialise_payload_fields_that_were_absent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var item = BuildInState(id, "submitted", TenantClientId);
        item.ReplacePayload(new BsonDocument
        {
            ["organisationName"] = "Acme Recycling Ltd",
            // Unmodelled key — must survive untouched.
            ["applicationReference"] = "RA-000000123",
        });
        await factory.SeedAsync(item, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/query", QueryBody(), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        var payload = persisted!.Payload;

        // The targeted $set adds currentQuery and nothing else: fields that
        // were absent before must still be absent, not explicit nulls.
        Assert.True(payload.Contains("currentQuery"));
        Assert.False(payload.Contains("accreditationId"));
        Assert.False(payload.Contains("accreditationStartDate"));
        Assert.False(payload.Contains("accreditationYear"));
        Assert.False(payload.Contains("slaClock"));
        // ... and unmodelled keys survive by construction, not by a merge.
        Assert.Equal("RA-000000123", payload["applicationReference"].AsString);
        Assert.Equal("Acme Recycling Ltd", payload["organisationName"].AsString);
    }

    [Fact]
    public async Task Query_reassigns_an_item_held_by_another_user_to_the_caller()
    {
        // RA-323 removed the assign-role tier: every caseworker may reassign
        // an item held by someone else. So querying an application assigned to
        // another user now succeeds, moves it to queried, and reassigns it to
        // the querying caseworker (which is what the query page promises).
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var item = BuildInState(id, "submitted", TenantClientId);
        item.AssignedToId = "bob-2";
        item.AssignedToName = "Bob Example";
        await factory.SeedAsync(item, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/query", QueryBody(), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("queried", persisted!.StateId);
        Assert.Equal(DefaultUserId, persisted.AssignedToId);
        Assert.Equal(DefaultUserName, persisted.AssignedToName);
        Assert.Contains(persisted.AuditLog,
            a => a.Action == ReAccreditationQueryService.AuditAction);
    }

    [Fact]
    public async Task Query_of_an_item_already_assigned_to_the_caller_writes_no_duplicate_assignment_audit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var item = BuildInState(id, "submitted", TenantClientId);
        item.AssignedToId = DefaultUserId;
        item.AssignedToName = DefaultUserName;
        await factory.SeedAsync(item, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/query", QueryBody(), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("queried", persisted!.StateId);
        Assert.Equal(DefaultUserId, persisted.AssignedToId);
        // Re-assigning to the same user is an idempotent no-op in the engine:
        // the query still succeeds, but no 'assigned' entry is written.
        Assert.DoesNotContain(persisted.AuditLog, a => a.Action == "assigned");
        Assert.Contains(persisted.AuditLog,
            a => a.Action == ReAccreditationQueryService.AuditAction);
    }

    [Fact]
    public async Task Query_returns_conflict_when_another_writer_wins_the_race()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        // Bump the on-disk version between the engine's load and replace so
        // the real optimistic-concurrency path fires.
        await using var factory = new ReAccreditationFactory(_fixture, raceWorkItemId: id);
        using var client = factory.CreateClient();

        await factory.SeedAsync(BuildInState(id, "submitted", TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/query", QueryBody(), cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("submitted", persisted!.StateId);
        Assert.DoesNotContain(persisted.AuditLog,
            a => a.Action == ReAccreditationQueryService.AuditAction);
    }

    [Fact]
    public async Task Query_returns_not_found_when_the_work_item_is_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{Guid.NewGuid()}/query", QueryBody(), cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Query_succeeds_for_a_work_item_not_submitted_by_the_caller()
    {
        // RBAC lives in the frontend now (ADR-0005) — the backend performs
        // the query regardless of who submitted the item.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildInState(id, "submitted", "a-different-tenant"), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/query", QueryBody(), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Query_returns_bad_request_for_a_work_item_of_another_type()
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
            $"/work-items/re-accreditation/{id}/query", QueryBody(), cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Query_returns_unauthorized_without_a_forwarded_user_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture, userId: null);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, "submitted", TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/query", QueryBody(), cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("submitted", persisted!.StateId);
    }

    [Theory]
    // sections omitted entirely / empty
    [InlineData(null, "why", "Select which areas you want to query")]
    [InlineData(new string[0], "why", "Select which areas you want to query")]
    // unknown section id, alone and alongside a valid one
    [InlineData(new[] { "not-a-section" }, "why", "Select a valid section to query")]
    [InlineData(new[] { "business-plan", "not-a-section" }, "why", "Select a valid section to query")]
    // reason missing / whitespace-only
    [InlineData(new[] { "business-plan" }, null, "Enter a reason for the query")]
    [InlineData(new[] { "business-plan" }, "   ", "Enter a reason for the query")]
    public async Task Query_rejects_an_invalid_body_before_touching_the_work_item(
        string[]? sections,
        string? reason,
        string expectedDetail)
    {
        var body = QueryBody(sections, reason);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, "submitted", TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/query", body, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal(expectedDetail, problem!.Detail);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("submitted", persisted!.StateId);
    }

    [Theory]
    // The 200-word cap is a shared contract with the frontend: 200 passes,
    // 201 does not.
    [InlineData(200, HttpStatusCode.OK)]
    [InlineData(201, HttpStatusCode.BadRequest)]
    public async Task Query_enforces_the_two_hundred_word_reason_cap(
        int wordCount,
        HttpStatusCode expected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, "submitted", TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/query",
            QueryBody(reason: string.Join(' ', Enumerable.Repeat("word", wordCount))),
            cancellationToken);

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.BadRequest)
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
            Assert.Equal(ReAccreditationQueryValidator.ReasonTooLongMessage, problem!.Detail);
        }
    }

    private static WorkItem BuildInState(Guid id, string stateId, string submittedBy)
    {
        var type = new ReAccreditationType();
        return new WorkItem
        {
            Id = id,
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            SubmittedBy = submittedBy,
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion
        };
    }

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
    public async Task Approve_succeeds_for_item_not_submitted_by_caller()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAwaitingDecision(id, "other-tenant"), cancellationToken);

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
        public Task<bool> SetPayloadFieldAsync(
            Guid workItemId,
            string fieldName,
            BsonValue value,
            CancellationToken cancellationToken = default) =>
            inner.SetPayloadFieldAsync(workItemId, fieldName, value, cancellationToken);

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
            Guid? raceWorkItemId = null)
        {
            _fixture = fixture;
            _clientId = clientId;
            _userId = userId;
            _userName = userName;
            _raceWorkItemId = raceWorkItemId;
        }

        public IWorkItemPersistence Persistence => Services.GetRequiredService<IWorkItemPersistence>();

        public Task SeedAsync(WorkItem item, CancellationToken cancellationToken)
        {
            EnsureProductionIndexes();
            return Persistence.CreateAsync(item, cancellationToken);
        }

        /// <summary>
        /// RA-291: force construction of every <c>MongoService</c> that owns
        /// indexes on the shared <c>workItems</c> collection, so integration
        /// tests run against the SAME index set production has.
        ///
        /// <see cref="IAccreditationIdLookup"/> is a lazily-constructed
        /// singleton, and indexes are created in the <c>MongoService</c>
        /// constructor — so unless something resolves it, its unique + sparse
        /// index on <c>payload.accreditationId</c> never exists in the test
        /// database. That is exactly why the duplicate-key bug in the
        /// current-query stamp reached the real stack with 968 tests green.
        /// </summary>
        private void EnsureProductionIndexes()
        {
            _ = Persistence;
            _ = Services.GetRequiredService<IAccreditationIdLookup>();
        }

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
