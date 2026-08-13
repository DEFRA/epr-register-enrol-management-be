using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
using EprRegisterEnrolManagementBe.Auth;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// epr-19h: re-accreditation module endpoints exercised through the real
/// ASP.NET pipeline (auth handler, routing, validation, ProblemDetails)
/// against ephemeral MongoDB. The decision service stays substituted —
/// it is the module's collaborator under test, not an infrastructure
/// boundary the integration suite is supposed to hit.
/// </summary>
public class ReAccreditationEndpointTests
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
            $"/work-items/re-accreditation/{Guid.NewGuid()}/recommendation",
            cancellationToken
        );

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
        await factory.SeedAsync(
            new WorkItem
            {
                Id = id,
                TypeId = "some-other-type",
                StateId = "submitted",
                SubmittedBy = TenantClientId,
            },
            cancellationToken
        );

        var response = await client.GetAsync(
            $"/work-items/re-accreditation/{id}/recommendation",
            cancellationToken
        );

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
            ["complianceIssuesReported"] = 1,
        };
        await factory.SeedAsync(
            new WorkItem
            {
                Id = id,
                TypeId = ReAccreditationType.Id,
                StateId = "submitted",
                SubmittedBy = TenantClientId,
                Payload = payload,
            },
            cancellationToken
        );

        ReAccreditationPayload? capturedPayload = null;
        factory
            .DecisionService.EvaluateRecommendation(
                Arg.Do<ReAccreditationPayload>(p => capturedPayload = p)
            )
            .Returns(
                new ReAccreditationRecommendation(
                    ReAccreditationRecommendation.Approve,
                    "Looks good"
                )
            );

        var response = await client.GetAsync(
            $"/work-items/re-accreditation/{id}/recommendation",
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReAccreditationRecommendationResponse>(
            cancellationToken
        );
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
        await factory.SeedAsync(
            new WorkItem
            {
                Id = id,
                TypeId = ReAccreditationType.Id,
                StateId = "submitted",
                SubmittedBy = TenantClientId,
                Payload = new BsonDocument(),
            },
            cancellationToken
        );
        factory
            .DecisionService.EvaluateRecommendation(Arg.Any<ReAccreditationPayload>())
            .Returns(
                new ReAccreditationRecommendation(
                    ReAccreditationRecommendation.MoreInfoNeeded,
                    "Missing fields"
                )
            );

        var response = await client.GetAsync(
            $"/work-items/re-accreditation/{id}/recommendation",
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReAccreditationRecommendationResponse>(
            cancellationToken
        );
        Assert.Equal(ReAccreditationRecommendation.MoreInfoNeeded, body!.Recommendation);
        factory
            .DecisionService.Received(1)
            .EvaluateRecommendation(Arg.Any<ReAccreditationPayload>());
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
                    email = "jane@example.com",
                },
                prns = new
                {
                    plannedTonnageBand = "UpTo1000",
                    authorisers = new[]
                    {
                        new { fullName = "Bob Jones", email = "bob@example.com" },
                    },
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
                    newUsesDetail = "Recycled packaging",
                },
                samplingPlan = new
                {
                    files = new[]
                    {
                        new
                        {
                            filename = "sampling-plan.pdf",
                            uploadedAt = DateTime.UtcNow,
                            scanStatus = "Clean",
                        },
                    },
                },
            },
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

    [Fact]
    public async Task Submit_round_trips_ra292_ors_interim_and_authoriser_fields_to_the_get_response()
    {
        // RA-292: AC01-AC04 are rendered by the case management frontend from
        // payload fields the operator backend produces. Nothing in this service
        // declares them — not ReAccreditationPayload, not WorkItemResponse — so
        // they survive only because the payload is schemaless from ingestion
        // (BsonDocument.Parse of the raw request JSON) through persistence to
        // the GET response (relaxed extended JSON).
        //
        // This is the end-to-end pin for that: submit a payload containing every
        // RA-292 field, including several this codebase has no type for, then
        // read it back over HTTP the way the BFF does. If a future typed model
        // is introduced anywhere on this path, the fields it fails to declare
        // stop reaching the regulator — and this test goes red instead.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var body = new
        {
            typeId = "re-accreditation",
            payload = new
            {
                organisationName = "Overseas Reprocessing Verification Ltd",
                registrationNumber = "EPR-100292",
                material = "plastic",
                operatorEmail = "ors.verification@example.com",
                siteAddressPostcode = "EC2A 2BB",
                overseasSites = new
                {
                    sites = new object[]
                    {
                        new
                        {
                            siteId = 1,
                            orsId = "ORS-2026-0292",
                            siteName = "Rotterdam New Reprocessing Site",
                            siteAddress = "1 Havenstraat, Rotterdam",
                            addressLine1 = "1 Havenstraat",
                            addressLine2 = "Europoort Industrial Park",
                            townOrCity = "Rotterdam",
                            country = "Netherlands",
                            coordinates = "51.9244, 4.4777",
                            contactName = "Johan de Vries",
                            contactEmail = "johan.devries@example.com",
                            contactPhone = "+31 10 123 4567",
                            operationCode = "R3",
                            code1 = "B3011",
                            code2 = "GH013",
                            code3 = "Y48",
                            // Producer types, verified against a captured
                            // payload: repatriatedLoads is a string and
                            // conditionsOfExport a nullable boolean.
                            repatriatedLoads = "3",
                            conditionsOfExport = true,
                            isEu = true,
                            isOecd = true,
                            isNewSite = true,
                            registeredNowAccredited = false,
                            besEvidence = new
                            {
                                files = new[]
                                {
                                    new { fileId = "bes-1", filename = "bes-evidence.pdf" },
                                },
                            },
                            interimSite = new
                            {
                                siteId = 11,
                                siteNumber = "INT-001",
                                isNewSite = true,
                                country = "Belgium",
                                siteName = "Antwerp Interim Holding Site",
                                addressLine1 = "12 Scheldelaan",
                                addressLine2 = "Unit 4",
                                townOrCity = "Antwerp",
                                stateOrRegion = "Flanders",
                                postcode = "2030",
                                contactName = "Elke Janssens",
                                contactEmail = "elke.janssens@example.com",
                                contactPhone = "+32 3 987 6543",
                            },
                        },
                        new
                        {
                            siteId = 2,
                            isNewSite = false,
                            interimSite = new { siteNumber = "INT-002", isNewSite = false },
                        },
                        // No isNewSite, no interimSite — the pre-RA-292 shape.
                        new { siteId = 3 },
                    },
                },
                prns = new
                {
                    authorisers = new object[]
                    {
                        new
                        {
                            fullName = "Grace Adeyemi",
                            email = "grace.adeyemi@example.com",
                            isNew = true,
                        },
                        new
                        {
                            fullName = "Martin Cole",
                            email = "martin.cole@example.com",
                            isNew = false,
                        },
                        new { fullName = "Priya Nair", email = "priya.nair@example.com" },
                    },
                },
            },
        };

        var created = await client.PostAsJsonAsync("/work-items", body, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdItem = await created.Content.ReadFromJsonAsync<WorkItemResponse>(
            cancellationToken
        );
        Assert.NotNull(createdItem);

        // Read it back the way the BFF does, rather than trusting the create
        // response — persistence is the step a typed model would sit in.
        var fetched = await client.GetAsync($"/work-items/{createdItem!.Id}", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        var item = await fetched.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        Assert.NotNull(item);

        var sites = item!.Payload.GetProperty("overseasSites").GetProperty("sites");
        Assert.Equal(3, sites.GetArrayLength());

        // AC04: every declared ORS detail field arrives intact.
        var newSite = sites[0];
        Assert.Equal("ORS-2026-0292", newSite.GetProperty("orsId").GetString());
        Assert.Equal("Rotterdam New Reprocessing Site", newSite.GetProperty("siteName").GetString());
        Assert.Equal("1 Havenstraat", newSite.GetProperty("addressLine1").GetString());
        Assert.Equal("Europoort Industrial Park", newSite.GetProperty("addressLine2").GetString());
        Assert.Equal("Rotterdam", newSite.GetProperty("townOrCity").GetString());
        Assert.Equal("Netherlands", newSite.GetProperty("country").GetString());
        Assert.Equal("51.9244, 4.4777", newSite.GetProperty("coordinates").GetString());
        Assert.Equal("Johan de Vries", newSite.GetProperty("contactName").GetString());
        Assert.Equal("johan.devries@example.com", newSite.GetProperty("contactEmail").GetString());
        Assert.Equal("+31 10 123 4567", newSite.GetProperty("contactPhone").GetString());
        Assert.Equal("R3", newSite.GetProperty("operationCode").GetString());
        Assert.Equal("B3011", newSite.GetProperty("code1").GetString());
        Assert.Equal("GH013", newSite.GetProperty("code2").GetString());
        Assert.Equal("Y48", newSite.GetProperty("code3").GetString());
        Assert.Equal("bes-evidence.pdf",
            newSite.GetProperty("besEvidence").GetProperty("files")[0]
                .GetProperty("filename").GetString());

        // Primitive types survive exactly as the producer sent them. The
        // frontend badge logic compares booleans by identity, so a boolean
        // arriving as a string (or vice versa) breaks it silently.
        Assert.Equal(JsonValueKind.True, newSite.GetProperty("isNewSite").ValueKind);
        Assert.Equal(JsonValueKind.True, newSite.GetProperty("isEu").ValueKind);
        Assert.Equal(JsonValueKind.True, newSite.GetProperty("isOecd").ValueKind);
        Assert.Equal(JsonValueKind.False, newSite.GetProperty("registeredNowAccredited").ValueKind);
        Assert.Equal(JsonValueKind.True, newSite.GetProperty("conditionsOfExport").ValueKind);
        Assert.Equal(JsonValueKind.String, newSite.GetProperty("repatriatedLoads").ValueKind);
        Assert.Equal("3", newSite.GetProperty("repatriatedLoads").GetString());
        Assert.Equal(JsonValueKind.Number, newSite.GetProperty("siteId").ValueKind);

        // AC02: the nested interim site — the deepest field, and the one a
        // shallow re-serialisation would drop first.
        var interim = newSite.GetProperty("interimSite");
        Assert.Equal(JsonValueKind.True, interim.GetProperty("isNewSite").ValueKind);
        Assert.Equal("INT-001", interim.GetProperty("siteNumber").GetString());
        Assert.Equal("Belgium", interim.GetProperty("country").GetString());
        Assert.Equal("Antwerp Interim Holding Site", interim.GetProperty("siteName").GetString());
        Assert.Equal("12 Scheldelaan", interim.GetProperty("addressLine1").GetString());
        Assert.Equal("Unit 4", interim.GetProperty("addressLine2").GetString());
        Assert.Equal("Antwerp", interim.GetProperty("townOrCity").GetString());
        Assert.Equal("Flanders", interim.GetProperty("stateOrRegion").GetString());
        Assert.Equal("2030", interim.GetProperty("postcode").GetString());
        Assert.Equal("Elke Janssens", interim.GetProperty("contactName").GetString());
        Assert.Equal("elke.janssens@example.com", interim.GetProperty("contactEmail").GetString());
        Assert.Equal("+32 3 987 6543", interim.GetProperty("contactPhone").GetString());

        Assert.Equal(JsonValueKind.False, sites[1].GetProperty("isNewSite").ValueKind);
        Assert.Equal(
            JsonValueKind.False,
            sites[1].GetProperty("interimSite").GetProperty("isNewSite").ValueKind
        );

        // Absent must stay absent, not be materialised as null or false.
        Assert.False(sites[2].TryGetProperty("isNewSite", out _));
        Assert.False(sites[2].TryGetProperty("interimSite", out _));

        // AC03: authority-to-issue contacts.
        var authorisers = item.Payload.GetProperty("prns").GetProperty("authorisers");
        Assert.Equal(3, authorisers.GetArrayLength());
        Assert.Equal("Grace Adeyemi", authorisers[0].GetProperty("fullName").GetString());
        Assert.Equal(JsonValueKind.True, authorisers[0].GetProperty("isNew").ValueKind);
        Assert.Equal(JsonValueKind.False, authorisers[1].GetProperty("isNew").ValueKind);
        Assert.False(authorisers[2].TryGetProperty("isNew", out _));
    }

    // -------------------- RecordDecisionRationale --------------------

    [Fact]
    public async Task RecordDecisionRationale_persists_a_note()
    {
        // RA-410: this endpoint used to also tick the record-decision-rationale
        // task in the same atomic write, gating approve/reject. The task
        // framework (and the gate) are gone, so the endpoint is now purely a
        // note write — see ReAccreditationEndpoints.RecordDecisionRationale.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildAwaitingDecision(id, TenantClientId),
            cancellationToken
        );

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision-rationale",
            new DecisionRationaleRequest("Approved on the basis of full compliance history."),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.NotNull(persisted);
        var note = Assert.Single(persisted!.Notes);
        Assert.StartsWith("[decision-rationale] ", note.Text);
        Assert.Equal(1, persisted.Version);
        var auditEntry = Assert.Single(persisted.AuditLog);
        Assert.Equal("note-added", auditEntry.Action);
        Assert.Equal(DefaultUserId, auditEntry.CreatedBy);
    }

    [Fact]
    public async Task RecordDecisionRationale_concurrency_conflict_persists_no_note()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        // Bump version on disk between the engine's load and replace so
        // the real optimistic-concurrency path fires (not a mocked throw).
        await using var factory = new ReAccreditationFactory(_fixture, raceWorkItemId: id);
        using var client = factory.CreateClient();

        await factory.SeedAsync(
            BuildAwaitingDecision(id, TenantClientId),
            cancellationToken
        );

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision-rationale",
            new DecisionRationaleRequest("Approved on the basis of full compliance history."),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.NotNull(persisted);
        Assert.Empty(persisted!.Notes);
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
            cancellationToken
        );

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
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RecordDecisionRationale_rejects_wrong_work_item_type()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            new WorkItem
            {
                Id = id,
                TypeId = "some-other-type",
                StateId = "submitted",
                SubmittedBy = TenantClientId,
            },
            cancellationToken
        );

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision-rationale",
            new DecisionRationaleRequest("Approved on the basis of full compliance history."),
            cancellationToken
        );

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
        await factory.SeedAsync(
            new WorkItem
            {
                Id = id,
                TypeId = ReAccreditationType.Id,
                StateId = "submitted",
                SubmittedBy = "other-tenant",
                Payload = new BsonDocument(),
            },
            cancellationToken
        );
        factory
            .DecisionService.EvaluateRecommendation(Arg.Any<ReAccreditationPayload>())
            .Returns(
                new ReAccreditationRecommendation(ReAccreditationRecommendation.Approve, "ok")
            );

        var response = await client.GetAsync(
            $"/work-items/re-accreditation/{id}/recommendation",
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RecordDecisionRationale_succeeds_for_item_not_submitted_by_caller()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildAwaitingDecision(id, "other-tenant"),
            cancellationToken
        );

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision-rationale",
            new DecisionRationaleRequest("Approved on the basis of full compliance history."),
            cancellationToken
        );

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
            $"/work-items/re-accreditation/{Guid.NewGuid()}/prior-year",
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await factory
            .ReExClient.DidNotReceiveWithAnyArgs()
            .GetPriorYearAsync(default, default, default, default);
    }

    [Fact]
    public async Task PriorYear_returns_problem_when_work_item_is_wrong_type()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            new WorkItem
            {
                Id = id,
                TypeId = "some-other-type",
                StateId = "submitted",
                SubmittedBy = TenantClientId,
            },
            cancellationToken
        );

        var response = await client.GetAsync(
            $"/work-items/re-accreditation/{id}/prior-year",
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PriorYear_returns_not_found_when_reex_returns_null()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        factory
            .ReExClient.GetPriorYearAsync(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult<PriorYearAccreditationDto?>(null));

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            new WorkItem
            {
                Id = id,
                TypeId = ReAccreditationType.Id,
                StateId = "submitted",
                SubmittedBy = TenantClientId,
                Payload = new BsonDocument
                {
                    ["operatorOrganisationId"] = "org-42",
                    ["operatorRegistrationId"] = "reg-99",
                    ["previousAccreditationYear"] = 2024,
                },
            },
            cancellationToken
        );

        var response = await client.GetAsync(
            $"/work-items/re-accreditation/{id}/prior-year",
            cancellationToken
        );

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
            Authorisers =
            [
                new PriorYearAuthoriserDto
                {
                    FullName = "Alice Smith",
                    Email = "alice@example.com",
                },
            ],
            BusinessPlan = new PriorYearBusinessPlanDto { NewInfrastructurePercent = 20 },
        };
        factory
            .ReExClient.GetPriorYearAsync("org-42", "reg-99", 2024, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PriorYearAccreditationDto?>(stubData));

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            new WorkItem
            {
                Id = id,
                TypeId = ReAccreditationType.Id,
                StateId = "submitted",
                SubmittedBy = TenantClientId,
                Payload = new BsonDocument
                {
                    ["operatorOrganisationId"] = "org-42",
                    ["operatorRegistrationId"] = "reg-99",
                    ["previousAccreditationYear"] = 2024,
                },
            },
            cancellationToken
        );

        var response = await client.GetAsync(
            $"/work-items/re-accreditation/{id}/prior-year",
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PriorYearAccreditationDto>(
            cancellationToken
        );
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
        factory
            .ReExClient.GetPriorYearAsync(
                Arg.Do<string?>(v => capturedOrgId = v),
                Arg.Do<string?>(v => capturedRegId = v),
                Arg.Do<int?>(v => capturedYear = v),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult<PriorYearAccreditationDto?>(null));

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            new WorkItem
            {
                Id = id,
                TypeId = ReAccreditationType.Id,
                StateId = "submitted",
                SubmittedBy = TenantClientId,
                Payload = new BsonDocument
                {
                    ["operatorOrganisationId"] = "org-77",
                    ["operatorRegistrationId"] = "reg-88",
                    ["previousAccreditationYear"] = 2023,
                },
            },
            cancellationToken
        );

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
        string expectedActionId
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, stateId, TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/query",
            QueryBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal("queried", persisted!.StateId);

        // The framework engine recorded the transition ...
        Assert.Contains(
            persisted.AuditLog,
            a =>
                a.Action == "action-applied"
                && a.Details.GetValueOrDefault("actionId") == expectedActionId
                && a.Details.GetValueOrDefault("fromStateId") == stateId
                && a.Details.GetValueOrDefault("toStateId") == "queried"
        );

        // ... and the module recorded what was actually asked for (AC05).
        var queryEntry = Assert.Single(
            persisted.AuditLog,
            a => a.Action == ReAccreditationQueryService.AuditAction
        );
        Assert.Equal(expectedActionId, queryEntry.Details.GetValueOrDefault("actionId"));
        Assert.Equal("business-plan,prn-tonnage", queryEntry.Details.GetValueOrDefault("sections"));
        Assert.Equal(
            "The tonnage figures do not reconcile with the sampling plan.",
            queryEntry.Details.GetValueOrDefault("reason")
        );
        Assert.Equal(DefaultUserId, queryEntry.CreatedBy);
        Assert.Equal(DefaultUserName, queryEntry.CreatedByName);

        // RA-291: the open query is stamped on the payload so the Queried
        // email can carry the reason. It must be exactly the reason recorded
        // on the audit entry — same record, one source of truth.
        var currentQuery = persisted.Payload!["currentQuery"].AsBsonDocument;
        Assert.Equal(DefaultQueryReason, currentQuery["reason"].AsString);
        Assert.Equal(
            ["business-plan", "prn-tonnage"],
            currentQuery["sections"].AsBsonArray.Select(v => v.AsString)
        );
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
        Assert.Contains(body.AuditLog!, a => a.Action == ReAccreditationQueryService.AuditAction);
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
            $"/work-items/re-accreditation/{id}/query",
            QueryBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.NotNull(problem);
        Assert.Contains(stateId, problem!.Detail);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(stateId, persisted!.StateId);
        Assert.DoesNotContain(
            persisted.AuditLog,
            a => a.Action == ReAccreditationQueryService.AuditAction
        );
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
        await factory.SeedAsync(
            BuildInState(first, "submitted", TenantClientId),
            cancellationToken
        );
        await factory.SeedAsync(
            BuildInState(second, "duly-made", TenantClientId),
            cancellationToken
        );

        var firstResponse = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{first}/query",
            QueryBody(),
            cancellationToken
        );
        var secondResponse = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{second}/query",
            QueryBody(),
            cancellationToken
        );

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
        item.ReplacePayload(
            new BsonDocument
            {
                ["organisationName"] = "Acme Recycling Ltd",
                // Unmodelled key — must survive untouched.
                ["applicationReference"] = "RA-000000123",
            }
        );
        await factory.SeedAsync(item, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/query",
            QueryBody(),
            cancellationToken
        );
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
            $"/work-items/re-accreditation/{id}/query",
            QueryBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("queried", persisted!.StateId);
        Assert.Equal(DefaultUserId, persisted.AssignedToId);
        Assert.Equal(DefaultUserName, persisted.AssignedToName);
        Assert.Contains(
            persisted.AuditLog,
            a => a.Action == ReAccreditationQueryService.AuditAction
        );
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
            $"/work-items/re-accreditation/{id}/query",
            QueryBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("queried", persisted!.StateId);
        Assert.Equal(DefaultUserId, persisted.AssignedToId);
        // Re-assigning to the same user is an idempotent no-op in the engine:
        // the query still succeeds, but no 'assigned' entry is written.
        Assert.DoesNotContain(persisted.AuditLog, a => a.Action == "assigned");
        Assert.Contains(
            persisted.AuditLog,
            a => a.Action == ReAccreditationQueryService.AuditAction
        );
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
            $"/work-items/re-accreditation/{id}/query",
            QueryBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("submitted", persisted!.StateId);
        Assert.DoesNotContain(
            persisted.AuditLog,
            a => a.Action == ReAccreditationQueryService.AuditAction
        );
    }

    [Fact]
    public async Task Query_returns_not_found_when_the_work_item_is_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{Guid.NewGuid()}/query",
            QueryBody(),
            cancellationToken
        );

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
            BuildInState(id, "submitted", "a-different-tenant"),
            cancellationToken
        );

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/query",
            QueryBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Query_returns_bad_request_for_a_work_item_of_another_type()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            new WorkItem
            {
                Id = id,
                TypeId = "some-other-type",
                StateId = "submitted",
                SubmittedBy = TenantClientId,
            },
            cancellationToken
        );

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/query",
            QueryBody(),
            cancellationToken
        );

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
            $"/work-items/re-accreditation/{id}/query",
            QueryBody(),
            cancellationToken
        );

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
    [InlineData(
        new[] { "business-plan", "not-a-section" },
        "why",
        "Select a valid section to query"
    )]
    // reason missing / whitespace-only
    [InlineData(new[] { "business-plan" }, null, "Enter a reason for the query")]
    [InlineData(new[] { "business-plan" }, "   ", "Enter a reason for the query")]
    public async Task Query_rejects_an_invalid_body_before_touching_the_work_item(
        string[]? sections,
        string? reason,
        string expectedDetail
    )
    {
        var body = QueryBody(sections, reason);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, "submitted", TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/query",
            body,
            cancellationToken
        );

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
        HttpStatusCode expected
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, "submitted", TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/query",
            QueryBody(reason: string.Join(' ', Enumerable.Repeat("word", wordCount))),
            cancellationToken
        );

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.BadRequest)
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(
                cancellationToken
            );
            Assert.Equal(ReAccreditationQueryValidator.ReasonTooLongMessage, problem!.Detail);
        }
    }

    // ------------------------- RA-311/MBE-1 ResumeFromQuery -------------------------

    private static ResumeFromQueryRequest ResumeBody() => ResumeBody(s_querySections);

    // Takes sectionKeys without defaulting an explicit null away, so callers
    // testing "sectionKeys omitted/empty" get exactly what they pass.
    private static ResumeFromQueryRequest ResumeBody(IReadOnlyList<string>? sectionKeys) =>
        new(
            new ResponderContactDetails("Jane Doe", "jane@example.com", "Manager"),
            sectionKeys,
            Sections: null,
            FileReferences:
            [
                new SectionFileReference(
                    "prn-tonnage",
                    "file-1",
                    "evidence.pdf",
                    "s3/key/evidence.pdf"
                ),
            ]
        );

    private static WorkItem BuildQueried(Guid id, string submittedBy, string queryActionId)
    {
        var item = BuildInState(id, "queried", submittedBy);
        item.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = ReAccreditationQueryService.AuditAction,
                ActionDisplayName = "Application queried",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = DefaultUserId,
                CreatedByName = DefaultUserName,
                Details = new Dictionary<string, string?> { ["actionId"] = queryActionId },
            }
        );
        return item;
    }

    [Theory]
    [InlineData("query-during-duly-making", "resume-during-duly-making")]
    [InlineData("query-during-duly-made", "resume-during-duly-made")]
    [InlineData("query-during-assessment", "resume-during-assessment")]
    [InlineData("query-during-decision", "resume-during-decision")]
    public async Task ResumeFromQuery_moves_the_application_to_updated(
        string queryActionId,
        string expectedResumeActionId
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildQueried(id, TenantClientId, queryActionId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/resume-from-query",
            ResumeBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.NotNull(persisted);
        // RA-337: resume-during-* lands on 'updated', not the originating
        // state, so CM shows an "Updated" status until a caseworker moves
        // it on via continue-review.
        Assert.Equal("updated", persisted!.StateId);
        Assert.Contains(
            persisted.AuditLog,
            a =>
                a.Action == "action-applied"
                && a.Details.GetValueOrDefault("actionId") == expectedResumeActionId
        );

        var responseEntry = Assert.Single(
            persisted.AuditLog,
            a => a.Action == ReAccreditationResumeService.AuditAction
        );
        Assert.Equal(
            "business-plan,prn-tonnage",
            responseEntry.Details.GetValueOrDefault("sectionKeys")
        );
        Assert.Equal("Jane Doe", responseEntry.Details.GetValueOrDefault("responderFullName"));

        var latestSections = persisted.Payload!["latestSections"].AsBsonDocument;
        Assert.Equal(
            ["business-plan", "prn-tonnage"],
            latestSections["sectionKeys"].AsBsonArray.Select(v => v.AsString)
        );
    }

    [Fact]
    public async Task ResumeFromQuery_is_idempotent_on_a_duplicate_call()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildQueried(id, TenantClientId, "query-during-duly-making"),
            cancellationToken
        );

        var first = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/resume-from-query",
            ResumeBody(),
            cancellationToken
        );
        var second = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/resume-from-query",
            ResumeBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("updated", persisted!.StateId);
    }

    [Fact]
    public async Task ResumeFromQuery_returns_conflict_when_another_writer_wins_the_race()
    {
        // Mirrors Query_returns_conflict_when_another_writer_wins_the_race:
        // proves the PR's "idempotent on a duplicate/concurrent resubmit
        // call" claim only holds for a genuinely SEQUENTIAL duplicate (the
        // second call observes the already-'updated' state before it starts
        // and takes the idempotent-replay branch). A truly concurrent
        // resubmit — another writer's replace lands between this request's
        // read and its own transition write — is not idempotent: the
        // engine's optimistic-concurrency check fires and this call gets a
        // clean 409, not a 200. That is a reasonable, safe outcome (the
        // caller can retry, and the retry *will* be an idempotent replay),
        // but it was previously entirely unproven — only the sequential
        // "call twice" case had a test.
        var cancellationToken = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await using var factory = new ReAccreditationFactory(_fixture, raceWorkItemId: id);
        using var client = factory.CreateClient();

        await factory.SeedAsync(
            BuildQueried(id, TenantClientId, "query-during-duly-making"),
            cancellationToken
        );

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/resume-from-query",
            ResumeBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("queried", persisted!.StateId);
        Assert.DoesNotContain(
            persisted.AuditLog,
            a => a.Action == ReAccreditationResumeService.AuditAction
        );
    }

    [Fact]
    public async Task ResumeFromQuery_returns_unauthorized_without_a_forwarded_user_id()
    {
        // New endpoint, same auth contract as every other mutating
        // re-accreditation endpoint (see Query_returns_unauthorized_...):
        // a missing 'user:id' claim must 401, not silently proceed or 500.
        // This was previously untested for resume-from-query specifically.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture, userId: null);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildQueried(id, TenantClientId, "query-during-duly-making"),
            cancellationToken
        );

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/resume-from-query",
            ResumeBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("queried", persisted!.StateId);
    }

    [Fact]
    public async Task ResumeFromQuery_returns_not_found_when_the_work_item_is_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{Guid.NewGuid()}/resume-from-query",
            ResumeBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ResumeFromQuery_succeeds_for_another_tenants_work_item()
    {
        // RBAC lives in the frontend now (ADR-0005) — the endpoint performs
        // the resume regardless of who submitted the item.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildQueried(id, "a-different-tenant", "query-during-duly-making"),
            cancellationToken
        );

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/resume-from-query",
            ResumeBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    public async Task ResumeFromQuery_returns_conflict_for_a_decided_outcome(string stateId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, stateId, TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/resume-from-query",
            ResumeBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData(null, "sectionKeys must contain at least one section")]
    [InlineData(new string[0], "sectionKeys must contain at least one section")]
    [InlineData(new[] { "not-a-section" }, "sectionKeys must only contain valid sections")]
    public async Task ResumeFromQuery_rejects_an_invalid_body_before_touching_the_work_item(
        string[]? sectionKeys,
        string expectedDetail
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildQueried(id, TenantClientId, "query-during-duly-making"),
            cancellationToken
        );

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/resume-from-query",
            ResumeBody(sectionKeys: sectionKeys),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal(expectedDetail, problem!.Detail);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("queried", persisted!.StateId);
    }

    // ------------------------------- RA-337 ContinueReview -------------------------------

    private static WorkItem BuildUpdated(Guid id, string submittedBy, string resumeActionId)
    {
        var item = BuildInState(id, "updated", submittedBy);
        item.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = "action-applied",
                ActionDisplayName = "Action applied",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = DefaultUserId,
                CreatedByName = DefaultUserName,
                Details = new Dictionary<string, string?>
                {
                    ["actionId"] = resumeActionId,
                    ["fromStateId"] = "queried",
                    ["toStateId"] = "updated",
                },
            }
        );
        return item;
    }

    [Theory]
    [InlineData("resume-during-duly-making", "continue-review-during-duly-making", "submitted")]
    [InlineData("resume-during-duly-made", "continue-review-during-duly-made", "duly-made")]
    [InlineData(
        "resume-during-assessment",
        "continue-review-during-assessment",
        "assessment-in-progress"
    )]
    [InlineData("resume-during-decision", "continue-review-during-decision", "awaiting-decision")]
    public async Task ContinueReview_moves_the_application_back_to_the_originating_state(
        string resumeActionId,
        string expectedContinueActionId,
        string expectedStateId
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildUpdated(id, TenantClientId, resumeActionId),
            cancellationToken
        );

        var response = await client.PostAsync(
            $"/work-items/re-accreditation/{id}/continue-review",
            content: null,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal(expectedStateId, persisted!.StateId);
        Assert.Contains(
            persisted.AuditLog,
            a =>
                a.Action == "action-applied"
                && a.Details.GetValueOrDefault("actionId") == expectedContinueActionId
        );
    }

    [Fact]
    public async Task ContinueReview_is_idempotent_on_a_duplicate_call()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildUpdated(id, TenantClientId, "resume-during-duly-making"),
            cancellationToken
        );

        var first = await client.PostAsync(
            $"/work-items/re-accreditation/{id}/continue-review",
            content: null,
            cancellationToken
        );
        var second = await client.PostAsync(
            $"/work-items/re-accreditation/{id}/continue-review",
            content: null,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("submitted", persisted!.StateId);
    }

    [Fact]
    public async Task ContinueReview_returns_conflict_when_another_writer_wins_the_race()
    {
        // Same concurrency argument as ResumeFromQuery_returns_conflict_...:
        // a genuinely concurrent continue-review (another writer's replace
        // lands between this request's read and its own transition write)
        // must not corrupt state or 500 — it should surface as a clean 409,
        // distinct from the sequential "call twice" idempotent-replay case
        // already covered by ContinueReview_is_idempotent_on_a_duplicate_call.
        var cancellationToken = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await using var factory = new ReAccreditationFactory(_fixture, raceWorkItemId: id);
        using var client = factory.CreateClient();

        await factory.SeedAsync(
            BuildUpdated(id, TenantClientId, "resume-during-duly-making"),
            cancellationToken
        );

        var response = await client.PostAsync(
            $"/work-items/re-accreditation/{id}/continue-review",
            content: null,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("updated", persisted!.StateId);
    }

    [Fact]
    public async Task ContinueReview_returns_unauthorized_without_a_forwarded_user_id()
    {
        // New endpoint, same auth contract as every other mutating
        // re-accreditation endpoint — previously untested for
        // continue-review specifically.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture, userId: null);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildUpdated(id, TenantClientId, "resume-during-duly-making"),
            cancellationToken
        );

        var response = await client.PostAsync(
            $"/work-items/re-accreditation/{id}/continue-review",
            content: null,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("updated", persisted!.StateId);
    }

    [Fact]
    public async Task ContinueReview_returns_not_found_when_the_work_item_is_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/work-items/re-accreditation/{Guid.NewGuid()}/continue-review",
            content: null,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    [InlineData("queried")]
    public async Task ContinueReview_returns_conflict_when_not_updated(string stateId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, stateId, TenantClientId), cancellationToken);

        var response = await client.PostAsync(
            $"/work-items/re-accreditation/{id}/continue-review",
            content: null,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ------------------------------- RA-294/RA-297 SiteAdded -------------------------------

    private static object OrsBody(string orsId = "001", bool isNewSite = true) =>
        new
        {
            siteType = "ors",
            orsId,
            siteNumber = (string?)null,
            isNewSite,
        };

    private static object InterimBody(
        string orsId = "001",
        string siteNumber = "INT-1",
        bool isNewSite = true
    ) =>
        new
        {
            siteType = "interim",
            orsId,
            siteNumber,
            isNewSite,
        };

    [Fact]
    public async Task SiteAdded_appends_a_site_added_audit_entry_for_an_ors_site()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildInState(id, "assessment-in-progress", TenantClientId),
            cancellationToken
        );

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/site-added",
            OrsBody(orsId: "001", isNewSite: true),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.NotNull(persisted);
        // No state transition — adding a site does not move the application on.
        Assert.Equal("assessment-in-progress", persisted!.StateId);

        var entry = Assert.Single(persisted.AuditLog, a => a.Action == "site-added");
        Assert.Equal("ors", entry.Details.GetValueOrDefault("siteType"));
        Assert.Equal("001", entry.Details.GetValueOrDefault("orsId"));
        Assert.Null(entry.Details.GetValueOrDefault("siteNumber"));
        Assert.Equal("True", entry.Details.GetValueOrDefault("isNewSite"));

        var body = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        Assert.NotNull(body);
        Assert.Contains(body!.AuditLog!, a => a.Action == "site-added");
    }

    [Fact]
    public async Task SiteAdded_appends_a_site_added_audit_entry_for_an_interim_site()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildInState(id, "assessment-in-progress", TenantClientId),
            cancellationToken
        );

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/site-added",
            InterimBody(orsId: "001", siteNumber: "INT-1", isNewSite: false),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        var entry = Assert.Single(persisted!.AuditLog, a => a.Action == "site-added");
        Assert.Equal("interim", entry.Details.GetValueOrDefault("siteType"));
        Assert.Equal("001", entry.Details.GetValueOrDefault("orsId"));
        Assert.Equal("INT-1", entry.Details.GetValueOrDefault("siteNumber"));
        Assert.Equal("False", entry.Details.GetValueOrDefault("isNewSite"));
    }

    [Fact]
    public async Task SiteAdded_returns_not_found_when_the_work_item_is_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{Guid.NewGuid()}/site-added",
            OrsBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("unknown-type", "001", null, "siteType must be 'ors' or 'interim'")]
    [InlineData("ors", null, null, "orsId is required")]
    [InlineData("interim", "001", null, "siteNumber is required when siteType is 'interim'")]
    public async Task SiteAdded_rejects_an_invalid_body_before_touching_the_work_item(
        string siteType,
        string? orsId,
        string? siteNumber,
        string expectedDetail
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildInState(id, "assessment-in-progress", TenantClientId),
            cancellationToken
        );

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/site-added",
            new
            {
                siteType,
                orsId,
                siteNumber,
                isNewSite = true,
            },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal(expectedDetail, problem!.Detail);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.DoesNotContain(persisted!.AuditLog, a => a.Action == "site-added");
    }

    [Fact]
    public async Task SiteAdded_succeeds_for_another_tenants_work_item()
    {
        // Same RBAC posture as ResumeFromQuery/ContinueReview (ADR-0005) —
        // this is a system-to-system notification from the operator backend,
        // not gated on which tenant submitted the item.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildInState(id, "assessment-in-progress", "a-different-tenant"),
            cancellationToken
        );

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/site-added",
            OrsBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SiteAdded_succeeds_without_a_forwarded_user_id()
    {
        // Unlike Query/Resume/ContinueReview, this is a system notification
        // from the operator backend rather than a case-worker action, so it
        // does not require an end-user identity — the audit entry's
        // CreatedBy/CreatedByName are simply null, mirroring the "system
        // entry" convention ReAccreditationPaymentService's own
        // notification-outcome audit entries use.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture, userId: null);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildInState(id, "assessment-in-progress", TenantClientId),
            cancellationToken
        );

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/site-added",
            OrsBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        var entry = Assert.Single(persisted!.AuditLog, a => a.Action == "site-added");
        Assert.Null(entry.CreatedBy);
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
            TemplateVersion = type.TemplateVersion,
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
            TemplateVersion = type.TemplateVersion,
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
                ["registrationNumber"] = "EX-001",
            },
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion,
        };
    }

    // -------------------- RA-316: Duly make endpoint --------------------

    /// <summary>
    /// The error vocabulary management-fe and the mgmt-tests e2e suite bind to.
    /// The frontend renders a GOV.UK error summary against the date input for
    /// any 400 whose errorCode starts "payment-date-", and treats everything
    /// else as a page-level failure — so these codes and statuses are a wire
    /// contract, not an implementation detail. They are asserted through the
    /// real HTTP pipeline because the ProblemDetails extension members are the
    /// thing under test, and those only exist once serialised.
    /// </summary>
    [Theory]
    [InlineData(null, "payment-date-required")]
    [InlineData("", "payment-date-required")]
    [InlineData("   ", "payment-date-required")]
    [InlineData("not-a-date", "payment-date-invalid")]
    [InlineData("2026-02-30", "payment-date-invalid")]
    [InlineData("15/07/2026", "payment-date-invalid")]
    [InlineData("2026-07-15T00:00:00Z", "payment-date-invalid")]
    [InlineData("2099-01-01", "payment-date-in-future")]
    [InlineData("1999-01-01", "payment-date-too-old")]
    public async Task DulyMake_rejects_a_bad_payment_date_with_a_bindable_error_code(
        string? paymentDate,
        string expectedErrorCode
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildDulyMakeCandidate(id, TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/duly-make",
            new { paymentDate },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal(expectedErrorCode, problem.GetProperty("errorCode").GetString());
        Assert.Equal("paymentDate", problem.GetProperty("field").GetString());
        Assert.Equal(
            "Could not complete duly making",
            problem.GetProperty("title").GetString()
        );

        // A rejected date changes nothing.
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("submitted", persisted!.StateId);
        Assert.Null(persisted.SlaClock);
        Assert.Empty(persisted.AuditLog);
    }

    [Fact]
    public async Task DulyMake_returns_ok_and_anchors_the_sla_to_the_payment_date()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildDulyMakeCandidate(id, TenantClientId), cancellationToken);
        var paymentDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-10);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/duly-make",
            new { paymentDate = paymentDate.ToString("yyyy-MM-dd") },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("duly-made", persisted!.StateId);
        Assert.Equal(
            paymentDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            persisted.SlaClock!.StartedAt
        );

        var payload = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<ReAccreditationPayload>(
            persisted.Payload
        );
        Assert.Equal(paymentDate, payload.PaymentDate);
    }

    [Fact]
    public async Task DulyMake_returns_not_found_for_missing_work_item()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{Guid.NewGuid()}/duly-make",
            new { paymentDate = "2026-07-15" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// A wrong-state failure is a 409, never a 400 — the frontend must show
    /// "this application has changed, reload" rather than a field error against
    /// the date the regulator typed, which was perfectly valid.
    /// </summary>
    [Fact]
    public async Task DulyMake_returns_conflict_for_an_item_in_the_wrong_state()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAssessmentInProgress(id, TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/duly-make",
            new { paymentDate = "2026-07-15" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>
    /// duly-make is declared CallerInvocable: false, so the generic engine
    /// route must refuse it. Otherwise a caller could reach duly-made without a
    /// payment date and therefore without an SLA clock, silently defeating the
    /// 12-week SLA.
    /// </summary>
    [Fact]
    public async Task The_generic_action_route_cannot_be_used_to_duly_make()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildDulyMakeCandidate(id, TenantClientId), cancellationToken);

        var response = await client.PostAsync(
            $"/work-items/{id}/actions/duly-make",
            content: null,
            cancellationToken
        );

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("submitted", persisted!.StateId);
        Assert.Null(persisted.SlaClock);
    }

    /// <summary>
    /// The dormant payment-completed endpoint was removed in RA-316 (no caller
    /// anywhere in the monorepo). Pinned so it is not resurrected by accident.
    /// </summary>
    [Fact]
    public async Task The_payment_completed_endpoint_no_longer_exists()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildDulyMakeCandidate(id, TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/payment-completed",
            new { paidAt = DateTime.UtcNow },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static WorkItem BuildDulyMakeCandidate(Guid id, string submittedBy)
    {
        var type = new ReAccreditationType();
        return new WorkItem
        {
            Id = id,
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            SubmittedBy = submittedBy,
            Payload = new BsonDocument
            {
                ["organisationName"] = "Acme Ltd",
                ["registrationNumber"] = "EX-001",
                ["applicationReference"] = "RA-123456789",
                ["chargeAmountPence"] = 327600,
            },
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion,
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
            $"/work-items/re-accreditation/{id}/approve",
            content: null,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal("approved", persisted!.StateId);
        // 1 for the approval ReplaceAsync, +1 for the queued publishing
        // audit, +1 for the notification hook's audit-sent entry.
        Assert.True(persisted.Version >= 1);
        var payload = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<ReAccreditationPayload>(
            persisted.Payload
        );
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
            $"/work-items/re-accreditation/{Guid.NewGuid()}/approve",
            content: null,
            cancellationToken
        );

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
            $"/work-items/re-accreditation/{id}/approve",
            content: null,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("approved", persisted!.StateId);
    }

    /// <summary>
    /// RA-346 / AC2 (superseded by RA-410): the bespoke approve endpoint sits
    /// outside the generic engine, so it never met the framework's
    /// task-completeness gate and used to enforce an equivalent IncompleteTasks
    /// check of its own, refusing a caseworker with 409 while
    /// <c>record-decision-rationale</c> was still outstanding. The task
    /// framework (and the gate) are gone, so the identical seed now simply
    /// succeeds — regression cover for the ungating.
    /// </summary>
    [Fact]
    public async Task Approve_succeeds_now_the_task_gate_is_removed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAwaitingDecision(id, TenantClientId), cancellationToken);

        var response = await client.PostAsync(
            $"/work-items/re-accreditation/{id}/approve",
            content: null,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal("approved", persisted!.StateId);
    }

    // --------------------------- RA-410: single-call decision ---------------------------

    /// <summary>
    /// AC03 / the whole point of the story: one call carries an application
    /// from <c>assessment-in-progress</c> to <c>approved</c>, discharging the
    /// <c>awaiting-decision</c> hop on the way, and the bespoke approval
    /// workflow still runs — an accreditation id is issued.
    /// </summary>
    [Fact]
    public async Task Decision_approves_in_one_call_from_assessment_and_issues_an_accreditation_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAssessmentInProgress(id, TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "approved" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal("approved", persisted!.StateId);
        Assert.True(persisted.Payload.TryGetValue("accreditationId", out var accreditationId));
        Assert.False(string.IsNullOrWhiteSpace(accreditationId.AsString));

        // The intermediate hop is invisible to the user but must remain a real
        // declared edge in the audit trail — start and end states look
        // identical whether the waypoint was discharged or jumped across.
        var actions = persisted
            .AuditLog.Where(e => e.Action == "action-applied")
            .Select(e => e.Details.GetValueOrDefault("actionId"))
            .ToList();
        Assert.Contains("submit-for-decision", actions);
        Assert.Contains("approve", actions);
    }

    [Fact]
    public async Task Decision_rejects_in_one_call_from_assessment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAssessmentInProgress(id, TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "rejected" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("rejected", persisted!.StateId);
    }

    /// <summary>
    /// An application already parked in <c>awaiting-decision</c> — by the
    /// pre-RA-410 two-step flow, or by a failure between the two hops — is
    /// finished by the identical call rather than needing a rescue path.
    /// </summary>
    [Fact]
    public async Task Decision_completes_an_application_stranded_in_awaiting_decision()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAwaitingDecision(id, TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "approved" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("approved", persisted!.StateId);
    }

    [Fact]
    public async Task Decision_replay_is_idempotent_and_flags_the_replay()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAssessmentInProgress(id, TenantClientId), cancellationToken);

        var first = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "approved" },
            cancellationToken
        );
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var issued = (await factory.Persistence.GetByIdAsync(id, cancellationToken))!
            .Payload.GetValue("accreditationId")
            .AsString;

        var replay = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "approved" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.Contains(WorkItemEndpoints.IdempotentReplayHeader));
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("approved", persisted!.StateId);
        // The accreditation id must not be re-issued on a replay.
        Assert.Equal(issued, persisted.Payload.GetValue("accreditationId").AsString);
    }

    /// <summary>
    /// The opposite outcome on a decided application is a conflict, never
    /// last-write-wins: reporting success would tell a caseworker their
    /// refusal landed on an application that is in fact approved.
    /// </summary>
    [Fact]
    public async Task Decision_with_a_conflicting_outcome_is_refused_and_changes_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAwaitingDecision(id, TenantClientId), cancellationToken);
        await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "approved" },
            cancellationToken
        );

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "rejected" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("approved", persisted!.StateId);
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Decision_rejects_an_unrecognised_outcome_with_a_bindable_error_code(
        string? outcome
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAssessmentInProgress(id, TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("invalid-outcome", problem.GetProperty("errorCode").GetString());
        Assert.Equal("outcome", problem.GetProperty("field").GetString());
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("assessment-in-progress", persisted!.StateId);
    }

    [Fact]
    public async Task Decision_is_refused_from_a_state_that_cannot_be_decided()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, "submitted", TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "approved" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("submitted", persisted!.StateId);
    }

    /// <summary>
    /// The mirror image of the frontend's own guard: a client older than v12
    /// does not know <c>reject</c> stopped being caller-invocable and can
    /// still post it. Only this server-side check stops a caseworker refusing
    /// an application without ever seeing the decision page, so it must refuse
    /// before any transition is applied.
    /// </summary>
    [Fact]
    public async Task Reject_is_rejected_server_side_as_not_caller_invocable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAwaitingDecision(id, TenantClientId), cancellationToken);

        var response = await client.PostAsync(
            $"/work-items/{id}/actions/reject",
            content: null,
            cancellationToken
        );

        Assert.True((int)response.StatusCode >= 400);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("awaiting-decision", persisted!.StateId);
    }

    /// <summary>
    /// RA-346 / AC1 (superseded by RA-410): <c>submit-for-decision</c> used to
    /// be filtered out of <c>availableActions</c> while any assessment task
    /// was outstanding. The task framework (and the gate) are gone, and
    /// <c>submit-for-decision</c> is now also <c>CallerInvocable: false</c>
    /// (RA-410 v12 — a decision is one call to <c>POST .../decision</c>), so
    /// it is never offered at all, gated or not.
    /// </summary>
    [Fact]
    public async Task SubmitForDecision_is_never_offered_as_a_caller_invocable_action()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAssessmentInProgress(id, TenantClientId), cancellationToken);

        var pending = await client.GetFromJsonAsync<WorkItemResponse>(
            $"/work-items/{id}",
            cancellationToken
        );
        Assert.NotNull(pending);
        Assert.DoesNotContain(
            pending!.AvailableActions,
            a => a.ActionId == "submit-for-decision"
        );
    }

    /// <summary>
    /// RA-346 / AC1 (superseded by RA-410): hiding the action is not the gate
    /// — a hand-crafted POST straight at the generic action endpoint must be
    /// rejected server-side too, and leave the work item untouched. Before
    /// RA-410 this was refused because assessment tasks were outstanding
    /// (409 IncompleteTasks); now it is refused because
    /// <c>submit-for-decision</c> is <c>CallerInvocable: false</c> — the
    /// same 400/"not declared" rejection the endpoint gives any caller who
    /// tries to invoke a non-invocable transition directly (RA-364/RA-311).
    /// </summary>
    [Fact]
    public async Task SubmitForDecision_is_rejected_server_side_as_not_caller_invocable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAssessmentInProgress(id, TenantClientId), cancellationToken);

        var response = await client.PostAsync(
            $"/work-items/{id}/actions/submit-for-decision",
            content: null,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal(
            "Action 'submit-for-decision' is not declared by work item type 're-accreditation'.",
            problem!.Detail
        );

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("assessment-in-progress", persisted!.StateId);
        Assert.Empty(persisted.AuditLog);
    }

    /// <summary>
    /// RA-346: pins the rest of the approve endpoint's failure-code mapping
    /// alongside the new IncompleteTasks arm — a mutation that no longer
    /// requires a forwarded user id must still 401 rather than fall through
    /// to the catch-all 400.
    /// </summary>
    [Fact]
    public async Task Approve_returns_unauthorized_without_a_forwarded_user_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture, userId: null);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAwaitingDecision(id, TenantClientId), cancellationToken);

        var response = await client.PostAsync(
            $"/work-items/re-accreditation/{id}/approve",
            content: null,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("awaiting-decision", persisted!.StateId);
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
            $"/work-items/re-accreditation/{id}/approve",
            content: null,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Wraps real persistence and runs <paramref name="onBeforeReplace"/>
    /// just before delegating to <see cref="ReplaceAsync"/> for a chosen
    /// work item id. Lets the test race a competing writer between the
    /// engine's load and replace so the real optimistic-concurrency path
    /// fires (not a mocked throw).
    /// </summary>
    private sealed class RacingPersistence(
        IWorkItemPersistence inner,
        Guid raceId,
        Func<Task> onBeforeReplace
    ) : IWorkItemPersistence
    {
        public Task<bool> SetPayloadFieldAsync(
            Guid workItemId,
            string fieldName,
            BsonValue value,
            CancellationToken cancellationToken = default
        ) => inner.SetPayloadFieldAsync(workItemId, fieldName, value, cancellationToken);

        public Task CreateAsync(WorkItem workItem, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(workItem, cancellationToken);

        public Task<bool> CreateIfAbsentAsync(
            WorkItem workItem,
            CancellationToken cancellationToken = default
        ) => inner.CreateIfAbsentAsync(workItem, cancellationToken);

        public Task<WorkItem?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default
        ) => inner.GetByIdAsync(id, cancellationToken);

        public Task<WorkItem?> FindByOperatorApplicationIdAsync(
            string typeId, string operatorApplicationId, CancellationToken cancellationToken = default
        ) => inner.FindByOperatorApplicationIdAsync(typeId, operatorApplicationId, cancellationToken);

        public Task<WorkItemPage> QueryAsync(
            WorkItemQuery query,
            CancellationToken cancellationToken = default
        ) => inner.QueryAsync(query, cancellationToken);

        public async Task ReplaceAsync(
            WorkItem workItem,
            CancellationToken cancellationToken = default
        )
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
            Guid? raceWorkItemId = null
        )
        {
            _fixture = fixture;
            _clientId = clientId;
            _userId = userId;
            _userName = userName;
            _raceWorkItemId = raceWorkItemId;
        }

        public IWorkItemPersistence Persistence =>
            Services.GetRequiredService<IWorkItemPersistence>();

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

                var clientFactory = new TestMongoDbClientFactory(
                    _fixture.ConnectionString,
                    _databaseName
                );
                services.AddSingleton<IMongoDbClientFactory>(clientFactory);

                services.AddSingleton<IWorkItemPersistence>(sp =>
                {
                    var real = new WorkItemPersistence(
                        clientFactory,
                        sp.GetRequiredService<ILoggerFactory>()
                    );
                    if (_raceWorkItemId is { } raceId)
                    {
                        return new RacingPersistence(
                            real,
                            raceId,
                            async () =>
                            {
                                // Mutate the on-disk doc so the engine's
                                // version-conditional ReplaceAsync misses.
                                var current = await real.GetByIdAsync(raceId);
                                if (current is not null)
                                {
                                    await real.ReplaceAsync(current);
                                }
                            }
                        );
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
            client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, _clientId);
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
