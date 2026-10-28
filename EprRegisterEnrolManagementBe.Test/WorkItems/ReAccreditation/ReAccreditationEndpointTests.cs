using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.Utils.Background;
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
{
    private static readonly string[] s_operationCodesR12 = ["R12"];
    private static readonly string[] s_operationCodesR3 = ["R3"];
    private static readonly string[] s_operationCodesR3AndR4 = ["R3", "R4"];
    private static readonly string[] s_operationCodesR5 = ["R5"];

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
                    plannedTonnageBand = "UpTo5000",
                    authorisers = new[]
                    {
                        new { fullName = "Bob Jones", email = "bob@example.com" },
                    },
                },
                businessPlan = new
                {
                    newInfrastructurePercent = 15,
                    priceSupportPercent = 15,
                    businessCollectionsPercent = 15,
                    communicationsPercent = 15,
                    newMarketsPercent = 10,
                    newUsesPercent = 10,
                    // RA-456: 7th category, "Activities or investment not
                    // covered by the other categories" — the other six
                    // percentages above were rebalanced (20/20/20/20/10/10 ->
                    // 15/15/15/15/10/10) so all seven still sum to 100.
                    otherPercent = 20,
                    newInfrastructureDetail = "New sorting line",
                    priceSupportDetail = "Subsidised collection",
                    businessCollectionsDetail = "Kerbside expansion",
                    communicationsDetail = "Customer newsletter",
                    newMarketsDetail = "Export contracts",
                    newUsesDetail = "Recycled packaging",
                    otherDetail = "Community recycling outreach programme",
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
    public async Task Submit_does_not_bind_OperatorEmail_from_a_misnamed_email_field()
    {
        // RA-123 regression guard. The operator address must arrive under
        // `operatorEmail`; a field-name mismatch (e.g. the frontend posting
        // `email`) must leave OperatorEmail null rather than silently binding,
        // so the notification flow skips rather than emailing the wrong value.
        //
        // This is the negative half of the contract the mgmt-tests
        // re-accreditation-operator-email-contract e2e spec used to observe via
        // the "Submission confirmation email sent" audit row. RA-422 disabled
        // that row (Notify:Enabled default false), so the contract is now pinned
        // here at the deserialisation boundary: the positive case is covered by
        // Submit_persists_every_field_from_a_real_operator_submission_payload
        // above; this test proves the wrong field name does not bind.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var body = new
        {
            typeId = ReAccreditationType.Id,
            source = "operator-fe",
            payload = new
            {
                organisationName = "Contract Test Organisation",
                registrationNumber = "EPR-100024",
                material = "plastic",
                // Deliberately the wrong field name — the RA-123 bug.
                email = "wrong-field@example.com",
            },
        };

        var response = await client.PostAsJsonAsync("/work-items", body, cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<WorkItemResponse>(cancellationToken);
        var persisted = await factory.Persistence.GetByIdAsync(created!.Id, cancellationToken);
        Assert.NotNull(persisted);

        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(persisted!.Payload);
        Assert.Null(payload.OperatorEmail);
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
        Assert.Equal(
            "Rotterdam New Reprocessing Site",
            newSite.GetProperty("siteName").GetString()
        );
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
        Assert.Equal(
            "bes-evidence.pdf",
            newSite
                .GetProperty("besEvidence")
                .GetProperty("files")[0]
                .GetProperty("filename")
                .GetString()
        );

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
        await factory.SeedAsync(BuildAwaitingDecision(id, TenantClientId), cancellationToken);

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

        await factory.SeedAsync(BuildAwaitingDecision(id, TenantClientId), cancellationToken);

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
        await factory.SeedAsync(BuildAwaitingDecision(id, "other-tenant"), cancellationToken);

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
            .GetPriorYearAsync(default, default, default, cancellationToken);
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
            TonnageBand = "UpTo5000",
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
        Assert.Equal("UpTo5000", body.TonnageBand);
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

    // -------------------- Malformed-payload / branch-coverage gaps --------------------

    [Fact]
    public async Task Recommendation_returns_problem_when_payload_fails_to_deserialise()
    {
        // The JsonException branch: a payload field whose stored BSON type
        // cannot bind onto the strongly-typed ReAccreditationPayload model
        // (a string where PreviousAccreditationYear expects an int) must
        // surface as a 400 ProblemDetails, not an unhandled 500.
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
                Payload = new BsonDocument { ["previousAccreditationYear"] = "not-a-number" },
            },
            cancellationToken
        );

        var response = await client.GetAsync(
            $"/work-items/re-accreditation/{id}/recommendation",
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal("Invalid re-accreditation payload", problem!.Title);
    }

    [Fact]
    public async Task PriorYear_returns_problem_when_payload_fails_to_deserialise()
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
                Payload = new BsonDocument { ["accreditationYear"] = "not-a-number" },
            },
            cancellationToken
        );

        var response = await client.GetAsync(
            $"/work-items/re-accreditation/{id}/prior-year",
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal("Invalid re-accreditation payload", problem!.Title);
    }

    [Fact]
    public async Task PriorYear_derives_the_prior_year_from_AccreditationYear_when_PreviousAccreditationYear_is_absent()
    {
        // Older work items only carry AccreditationYear (Year N); the
        // prior-year lookup must derive Year - 1 rather than passing null.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        int? capturedYear = null;
        factory
            .ReExClient.GetPriorYearAsync(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
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
                Payload = new BsonDocument { ["accreditationYear"] = 2026 },
            },
            cancellationToken
        );

        await client.GetAsync($"/work-items/re-accreditation/{id}/prior-year", cancellationToken);

        Assert.Equal(2025, capturedYear);
    }

    [Fact]
    public async Task PriorYear_passes_a_null_year_when_neither_year_field_is_present()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        int? capturedYear = -1; // sentinel distinct from null so we can tell it was overwritten
        factory
            .ReExClient.GetPriorYearAsync(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
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
                Payload = new BsonDocument(),
            },
            cancellationToken
        );

        await client.GetAsync($"/work-items/re-accreditation/{id}/prior-year", cancellationToken);

        Assert.Null(capturedYear);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task RecordDecisionRationale_null_or_whitespace_rationale_is_rejected(
        string? rationale
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision-rationale",
            new DecisionRationaleRequest(rationale!),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal("'rationale' is required and must not be whitespace.", problem!.Detail);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Null(persisted);
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
    [InlineData("withdrawn")]
    // RA-527: nor one whose determination is made but not yet applied.
    [InlineData("approved-pending-contact")]
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
    // RA-534: a missing / whitespace-only reason is no longer rejected here —
    // see Query_accepts_a_missing_reason_and_records_it_as_empty below.
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

    [Theory]
    // RA-534: the reason is optional. An omitted or whitespace-only reason
    // is a valid query; it is normalised to an empty string on both the
    // audit entry and the stamped CurrentQuery.
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Query_accepts_a_missing_reason_and_records_it_as_empty(string? reason)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, "submitted", TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/query",
            QueryBody(reason: reason),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("queried", persisted!.StateId);

        var queryEntry = Assert.Single(
            persisted.AuditLog,
            a => a.Action == ReAccreditationQueryService.AuditAction
        );
        Assert.Equal("business-plan,prn-tonnage", queryEntry.Details.GetValueOrDefault("sections"));
        Assert.Equal(string.Empty, queryEntry.Details.GetValueOrDefault("reason"));

        Assert.Equal(string.Empty, persisted.Payload!["currentQuery"]["reason"].AsString);
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
    // RA-523: resume-during-duly-made lands in assessment-in-progress
    // (decision-ready); the other three still land on the 'updated' waypoint.
    [InlineData("query-during-duly-making", "resume-during-duly-making", "updated")]
    [InlineData("query-during-duly-made", "resume-during-duly-made", "assessment-in-progress")]
    [InlineData("query-during-assessment", "resume-during-assessment", "updated")]
    [InlineData("query-during-decision", "resume-during-decision", "updated")]
    public async Task ResumeFromQuery_moves_the_application_to_its_resume_destination(
        string queryActionId,
        string expectedResumeActionId,
        string expectedStateId
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
        // RA-337/RA-523: three resume-during-* land on 'updated'; the
        // duly-made origin lands decision-ready in 'assessment-in-progress'.
        Assert.Equal(expectedStateId, persisted!.StateId);
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
    public async Task ResumeFromQuery_accepts_a_body_larger_than_64_KiB()
    {
        // RA-696: a resubmit of a large application (many overseas sites with
        // interim sites, BES evidence) is far bigger than 64 KiB but well within
        // the 1 MiB that initial submit already allows. The in-memory test
        // server does not enforce the Kestrel request size cap (see
        // WorkItemRequestSizeLimitTests), which that class pins separately;
        // this proves the endpoint itself handles a body of that size.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildQueried(id, TenantClientId, "query-during-assessment"),
            cancellationToken
        );

        var sites = string.Join(
            ",",
            Enumerable
                .Range(1, 120)
                .Select(i =>
                    $$"""{"siteId":{{i}},"orsId":"{{i:D3}}","siteName":"{{new string('x', 1000)}}"}"""
                )
        );
        var request = new ResumeFromQueryRequest(
            new ResponderContactDetails("Jane Doe", "jane@example.com", "Manager"),
            ["overseas-reprocessing-sites"],
            new Dictionary<string, JsonElement>
            {
                ["OverseasSites"] = JsonDocument.Parse($$"""{"sites":[{{sites}}]}""").RootElement,
            },
            FileReferences: []
        );
        var size = JsonSerializer.SerializeToUtf8Bytes(request).Length;
        Assert.InRange(size, (64 * 1024) + 1, 1024 * 1024);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/resume-from-query",
            request,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(120, persisted!.Payload!["overseasSites"]["sites"].AsBsonArray.Count);
    }

    [Fact]
    public async Task ResumeFromQuery_updates_the_canonical_overseas_sites_field_when_a_site_is_removed()
    {
        // RA-557 regression: a resubmitted "OverseasSites" section (the
        // operator backend's OperatorSection enum name) previously landed
        // only in latestSections, so a site removed via query resubmit
        // never reached payload.overseasSites.sites — the field the case
        // management summary page actually reads.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var item = BuildQueried(id, TenantClientId, "query-during-assessment");
        item.Payload = new BsonDocument
        {
            ["overseasSites"] = new BsonDocument
            {
                ["sites"] = new BsonArray
                {
                    new BsonDocument { ["siteId"] = 1, ["orsId"] = "ORS-2026-0001" },
                    new BsonDocument { ["siteId"] = 2, ["orsId"] = "ORS-2026-0002" },
                },
            },
        };
        await factory.SeedAsync(item, cancellationToken);

        var request = new ResumeFromQueryRequest(
            new ResponderContactDetails("Jane Doe", "jane@example.com", "Manager"),
            ["overseas-reprocessing-sites"],
            new Dictionary<string, JsonElement>
            {
                ["OverseasSites"] = JsonDocument.Parse(
                    """{"sites":[{"siteId":1,"orsId":"ORS-2026-0001"}]}""").RootElement,
            },
            FileReferences: []
        );

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/resume-from-query",
            request,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        var sites = persisted!.Payload!["overseasSites"]["sites"].AsBsonArray;
        var site = Assert.Single(sites);
        Assert.Equal("ORS-2026-0001", site["orsId"].AsString);
    }

    /// <summary>
    /// RA-523 helper: drive resume-from-query the way production does — the
    /// operator backend (HttpCaseWorkingApiAdapter) calls it as the OPERATOR,
    /// sending the responder's email / full name as x-cdp-user-id /
    /// x-cdp-user-name, never as the case worker who raised the query.
    /// </summary>
    private static async Task<HttpResponseMessage> ResumeAsOperatorAsync(
        HttpClient client,
        Guid id,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/work-items/re-accreditation/{id}/resume-from-query"
        )
        {
            Content = JsonContent.Create(ResumeBody()),
        };
        request.Headers.Remove("x-cdp-user-id");
        request.Headers.Remove("x-cdp-user-name");
        request.Headers.Add("x-cdp-user-id", "olive@operator.example.com");
        request.Headers.Add("x-cdp-user-name", "Olive Operator");
        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// RA-523 AC01/AC02: the case worker queries an application that nobody
    /// owns, the operator resubmits, and the application comes back to the
    /// Case Management service in the querying case worker's hands — end to
    /// end through the real query and
    /// resume endpoints, with the resume driven by the operator as it is in
    /// production.
    /// </summary>
    [Fact]
    public async Task ResumeFromQuery_leaves_an_unassigned_queried_item_with_the_querying_case_worker()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var seeded = BuildInState(id, "submitted", TenantClientId);
        Assert.Null(seeded.AssignedToId);
        await factory.SeedAsync(seeded, cancellationToken);

        var queryResponse = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/query",
            QueryBody(),
            cancellationToken
        );
        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);

        // AC01: querying an unassigned application self-assigns it (RA-291).
        var afterQuery = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(DefaultUserId, afterQuery!.AssignedToId);

        var resumeResponse = await ResumeAsOperatorAsync(client, id, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, resumeResponse.StatusCode);

        // AC03: the state change is unchanged.
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("updated", persisted!.StateId);

        // AC02: still the querying case worker's, and visible on the read model
        // the BFF consumes.
        Assert.Equal(DefaultUserId, persisted.AssignedToId);
        Assert.Equal(DefaultUserName, persisted.AssignedToName);

        var body = await resumeResponse.Content.ReadFromJsonAsync<WorkItemResponse>(
            cancellationToken
        );
        Assert.Equal(DefaultUserId, body!.AssignedToId);
        Assert.Equal(DefaultUserName, body.AssignedToName);

        var detail = await client.GetFromJsonAsync<WorkItemResponse>(
            $"/work-items/{id}",
            cancellationToken
        );
        Assert.Equal(DefaultUserId, detail!.AssignedToId);
    }

    /// <summary>
    /// RA-523 reproduction. The Case Management service offers unassign unconditionally on any
    /// non-terminal item, <c>queried</c> included, so the RA-291 self-assign is
    /// not durable across the query window. Before this fix nothing
    /// re-established ownership when the operator resubmitted, so the
    /// application came back <c>updated</c> and owned by nobody — exactly what
    /// the bug reports. AC02/AC05.
    /// </summary>
    [Fact]
    public async Task ResumeFromQuery_restores_the_querying_case_worker_when_the_item_was_unassigned()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, "submitted", TenantClientId), cancellationToken);

        var queryResponse = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/query",
            QueryBody(),
            cancellationToken
        );
        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);

        // The query window: somebody drops the application while it waits on
        // the operator. The Case Management service allows this on a queried item.
        var unassign = await client.PostAsync(
            $"/work-items/{id}/unassign",
            content: null,
            cancellationToken
        );
        Assert.Equal(HttpStatusCode.OK, unassign.StatusCode);
        var whileQueried = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Null(whileQueried!.AssignedToId);

        var resumeResponse = await ResumeAsOperatorAsync(client, id, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, resumeResponse.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("updated", persisted!.StateId);
        Assert.Equal(DefaultUserId, persisted.AssignedToId);
        Assert.Equal(DefaultUserName, persisted.AssignedToName);

        // AC05: the restore is audited like any other assignment, and names the
        // case worker it went to rather than silently mutating the envelope.
        var assigned = persisted.AuditLog.Last(a => a.Action == "assigned");
        Assert.Equal(DefaultUserId, assigned.Details.GetValueOrDefault("assigneeId"));
        Assert.Equal(DefaultUserName, assigned.Details.GetValueOrDefault("assigneeName"));

        var body = await resumeResponse.Content.ReadFromJsonAsync<WorkItemResponse>(
            cancellationToken
        );
        Assert.Equal(DefaultUserId, body!.AssignedToId);
    }

    /// <summary>
    /// RA-523: an application a colleague deliberately took over mid-query
    /// stays theirs. An operator-driven resubmission must not silently undo a
    /// human re-assignment.
    /// </summary>
    [Fact]
    public async Task ResumeFromQuery_leaves_an_item_reassigned_mid_query_with_its_new_owner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, "submitted", TenantClientId), cancellationToken);

        var queryResponse = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/query",
            QueryBody(),
            cancellationToken
        );
        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);

        var reassign = await client.PostAsJsonAsync(
            $"/work-items/{id}/assign",
            new { assigneeId = "bob-2", assigneeName = "Bob Example" },
            cancellationToken
        );
        Assert.Equal(HttpStatusCode.OK, reassign.StatusCode);

        var resumeResponse = await ResumeAsOperatorAsync(client, id, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, resumeResponse.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("updated", persisted!.StateId);
        Assert.Equal("bob-2", persisted.AssignedToId);
        Assert.Equal("Bob Example", persisted.AssignedToName);
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

    /// <summary>
    /// RA-523: the whole point of the ticket. An application queried after it
    /// was duly made must, on the operator's response, arrive decision-ready in
    /// 'assessment-in-progress' (not the 'updated' waypoint), be handed back to
    /// the case worker who raised the query, and keep the SLA clock that was
    /// anchored at duly making — no forward button, no return to 'duly-made'.
    /// </summary>
    [Fact]
    public async Task ResumeFromQuery_lands_a_duly_made_origin_item_decision_ready_assigned_with_its_clock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var seeded = BuildQueried(id, TenantClientId, "query-during-duly-made");
        // The query was raised by DefaultUserId (BuildQueried stamps CreatedBy),
        // then the item was left unassigned during the query window.
        seeded.AssignedToId = null;
        seeded.AssignedToName = null;
        var slaStartedAt = new DateTime(2025, 11, 3, 0, 0, 0, DateTimeKind.Utc);
        seeded.SlaClock = new WorkItemSlaClock { StartedAt = slaStartedAt };
        await factory.SeedAsync(seeded, cancellationToken);

        var response = await ResumeAsOperatorAsync(client, id, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.NotNull(persisted);
        // Decision-ready, not the 'updated' waypoint, and never back to 'duly-made'.
        Assert.Equal("assessment-in-progress", persisted!.StateId);
        // Re-assigned to the querying case worker (the parent RA-523 fix, which
        // runs in the resume regardless of landing state).
        Assert.Equal(DefaultUserId, persisted.AssignedToId);
        // The SLA clock anchored at duly making is untouched: no restart,
        // no second sla-clock-started entry.
        Assert.NotNull(persisted.SlaClock);
        Assert.Equal(slaStartedAt, persisted.SlaClock!.StartedAt);
        Assert.DoesNotContain(persisted.AuditLog, a => a.Action == "sla-clock-started");
        // The forward hop is the resume itself — no continue-review / payment hop.
        Assert.DoesNotContain(
            persisted.AuditLog,
            a => a.Details.GetValueOrDefault("actionId") == "continue-review-during-duly-made"
        );
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

    // -------------------- RA-252: Withdraw endpoint --------------------

    private static WithdrawApplicationRequest WithdrawBody(
        string? reason = "The operator no longer wishes to proceed with this application."
    ) => new(reason);

    [Theory]
    [InlineData("submitted")]
    [InlineData("duly-made")]
    [InlineData("assessment-in-progress")]
    [InlineData("awaiting-decision")]
    [InlineData("queried")]
    [InlineData("updated")]
    public async Task Withdraw_moves_the_application_to_withdrawn_and_records_the_reason_as_a_note(
        string stateId
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, stateId, TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/withdraw",
            WithdrawBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal("withdrawn", persisted!.StateId);
        Assert.Contains(
            persisted.Notes,
            n => n.Text == "The operator no longer wishes to proceed with this application."
        );
    }

    [Fact]
    public async Task Withdraw_is_idempotent_when_the_application_is_already_withdrawn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, "withdrawn", TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/withdraw",
            WithdrawBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("withdrawn", persisted!.StateId);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    // RA-527: withdrawing would orphan the minted accreditation number.
    [InlineData("approved-pending-contact")]
    public async Task Withdraw_returns_conflict_for_a_decided_outcome(string stateId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, stateId, TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/withdraw",
            WithdrawBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(stateId, persisted!.StateId);
    }

    [Fact]
    public async Task Withdraw_returns_not_found_when_the_work_item_is_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{Guid.NewGuid()}/withdraw",
            WithdrawBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Withdraw_returns_bad_request_for_a_work_item_of_another_type()
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
            $"/work-items/re-accreditation/{id}/withdraw",
            WithdrawBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Withdraw_returns_unauthorized_without_a_forwarded_user_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture, userId: null);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, "submitted", TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/withdraw",
            WithdrawBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("submitted", persisted!.StateId);
    }

    [Fact]
    public async Task Withdraw_succeeds_for_a_work_item_not_submitted_by_the_caller()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildInState(id, "submitted", "a-different-tenant"),
            cancellationToken
        );

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/withdraw",
            WithdrawBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null, "Enter a reason for the withdrawal")]
    [InlineData("   ", "Enter a reason for the withdrawal")]
    public async Task Withdraw_rejects_an_invalid_body_before_touching_the_work_item(
        string? reason,
        string expectedDetail
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, "submitted", TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/withdraw",
            WithdrawBody(reason),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal(expectedDetail, problem!.Detail);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("submitted", persisted!.StateId);
    }

    [Fact]
    public async Task Withdraw_enforces_the_two_hundred_word_reason_cap()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, "submitted", TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/withdraw",
            WithdrawBody(string.Join(' ', Enumerable.Repeat("word", 201))),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal(ReAccreditationWithdrawValidator.ReasonTooLongMessage, problem!.Detail);
    }

    [Fact]
    public async Task Withdraw_returns_conflict_when_another_writer_wins_the_race()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await using var factory = new ReAccreditationFactory(_fixture, raceWorkItemId: id);
        using var client = factory.CreateClient();

        await factory.SeedAsync(BuildInState(id, "submitted", TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/withdraw",
            WithdrawBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("submitted", persisted!.StateId);
    }

    // -------------------- DulyMake: remaining failure-code branches --------------------

    [Fact]
    public async Task DulyMake_returns_unauthorized_without_a_forwarded_user_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture, userId: null);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildDulyMakeCandidate(id, TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/duly-make",
            new { paymentDate = "2026-07-15" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("submitted", persisted!.StateId);
    }

    [Fact]
    public async Task DulyMake_returns_bad_request_with_wrong_work_item_type_error_code_for_another_type()
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
            $"/work-items/re-accreditation/{id}/duly-make",
            new { paymentDate = "2026-07-15" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("wrong-work-item-type", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task DulyMake_returns_conflict_when_another_writer_wins_the_race()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await using var factory = new ReAccreditationFactory(_fixture, raceWorkItemId: id);
        using var client = factory.CreateClient();

        await factory.SeedAsync(BuildDulyMakeCandidate(id, TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/duly-make",
            new { paymentDate = "2026-07-15" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("submitted", persisted!.StateId);
    }

    [Fact]
    public async Task DulyMake_treats_a_null_request_body_as_a_missing_payment_date()
    {
        // request is nullable ([FromBody] DulyMakeRequest?) precisely so a
        // JSON 'null' body reaches the endpoint's own validation instead of
        // failing model binding — exercises the request?.PaymentDate branch.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildDulyMakeCandidate(id, TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync<object?>(
            $"/work-items/re-accreditation/{id}/duly-make",
            null,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("payment-date-required", problem.GetProperty("errorCode").GetString());
    }

    // -------------------- LogDecision: remaining failure-code branches --------------------

    [Fact]
    public async Task Decision_returns_unauthorized_without_a_forwarded_user_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture, userId: null);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAssessmentInProgress(id, TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "approved" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("assessment-in-progress", persisted!.StateId);
    }

    [Fact]
    public async Task Decision_returns_bad_request_with_wrong_work_item_type_error_code_for_another_type()
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
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "approved" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("wrong-work-item-type", problem.GetProperty("errorCode").GetString());
    }

    private static IOperatorBackendPushAdapter DecisionPushAdapterReturning(
        params OperatorBackendPushResult[] results
    )
    {
        var adapter = Substitute.For<IOperatorBackendPushAdapter>();
        adapter
            .PushDecisionStatusChangedAsync(
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(results[0], results[1..]);
        return adapter;
    }

    [Fact]
    public async Task Decision_returns_not_found_for_missing_work_item()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{Guid.NewGuid()}/decision",
            new { outcome = "approved" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Decision_treats_a_null_request_body_as_an_invalid_outcome()
    {
        // request is nullable ([FromBody] LogDecisionRequest?) so a JSON
        // 'null' body reaches TryParseOutcome's request?.Outcome branch
        // rather than failing model binding.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAssessmentInProgress(id, TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync<object?>(
            $"/work-items/re-accreditation/{id}/decision",
            null,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("invalid-outcome", problem.GetProperty("errorCode").GetString());
    }

    // -------------------- ResumeFromQuery / ContinueReview / SiteAdded: wrong-type + race --------------------

    [Fact]
    public async Task ResumeFromQuery_returns_bad_request_for_a_work_item_of_another_type()
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
                StateId = "queried",
                SubmittedBy = TenantClientId,
            },
            cancellationToken
        );

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/resume-from-query",
            ResumeBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ContinueReview_returns_bad_request_for_a_work_item_of_another_type()
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
                StateId = "updated",
                SubmittedBy = TenantClientId,
            },
            cancellationToken
        );

        var response = await client.PostAsync(
            $"/work-items/re-accreditation/{id}/continue-review",
            content: null,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SiteAdded_returns_bad_request_for_a_work_item_of_another_type()
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
                StateId = "assessment-in-progress",
                SubmittedBy = TenantClientId,
            },
            cancellationToken
        );

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/site-added",
            OrsBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SiteAdded_returns_conflict_when_another_writer_wins_the_race()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await using var factory = new ReAccreditationFactory(_fixture, raceWorkItemId: id);
        using var client = factory.CreateClient();

        await factory.SeedAsync(
            BuildInState(id, "assessment-in-progress", TenantClientId),
            cancellationToken
        );

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/site-added",
            OrsBody(),
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.DoesNotContain(persisted!.AuditLog, a => a.Action == "site-added");
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
            // RA-448 phase 2: required for the approve step's call to
            // IAccreditationNumberAdapter.
            Payload = new BsonDocument
            {
                ["operatorOrganisationId"] = "500027",
                ["operatorApplicationId"] = "APP-500027",
                ["nation"] = "England",
            },
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
                // RA-448 phase 2: required for the approve step's call to
                // IAccreditationNumberAdapter.
                ["operatorOrganisationId"] = "500027",
                ["operatorApplicationId"] = "APP-500027",
                ["nation"] = "England",
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
        Assert.Equal("Could not complete duly making", problem.GetProperty("title").GetString());

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

    // -------------------- RA-595: Assessment started --------------------

    private static readonly DateTime s_dulyMadeClockStart = new(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);

    private static WorkItem BuildDulyMade(Guid id, string submittedBy, string? assignedToId = null)
    {
        var item = BuildInState(id, "duly-made", submittedBy);
        item.Payload = new BsonDocument
        {
            ["operatorOrganisationId"] = "500027",
            ["operatorApplicationId"] = $"APP-{id:N}",
            ["paymentDate"] = "2026-08-20",
        };
        item.SlaClock = new WorkItemSlaClock { StartedAt = s_dulyMadeClockStart };
        item.AssignedToId = assignedToId;
        item.AssignedToName = assignedToId is null ? null : "Bob Colleague";
        return item;
    }

    /// <summary>
    /// A push adapter whose status-changed pushes complete a task, so a test
    /// can await the push the status hook defers onto the background queue.
    /// </summary>
    private static (IOperatorBackendPushAdapter Adapter, Task<(string ActionId, string ToStateId, string ActionDisplayName)> Pushed)
        CapturingPushAdapter()
    {
        var pushed = new TaskCompletionSource<(string, string, string)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = Substitute.For<IOperatorBackendPushAdapter>();
        adapter
            .PushStatusChangedAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                pushed.TrySetResult((call.ArgAt<string>(5), call.ArgAt<string>(3), call.ArgAt<string>(6)));
                return Task.FromResult(OperatorBackendPushResult.Success());
            });
        return (adapter, pushed.Task);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("bob-1")]
    public async Task Start_assessment_moves_a_duly_made_case_into_assessment_and_pushes_updated(
        string? assignedToId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (adapter, pushed) = CapturingPushAdapter();
        await using var factory = new ReAccreditationFactory(_fixture, pushAdapter: adapter);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildDulyMade(id, TenantClientId, assignedToId), cancellationToken);

        var response = await client.PostAsync(
            $"/work-items/{id}/actions/start-assessment",
            content: null,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("assessment-in-progress", persisted!.StateId);
        var entry = Assert.Single(persisted.AuditLog, a => a.Action == "action-applied");
        Assert.Equal("start-assessment", entry.Details.GetValueOrDefault("actionId"));
        Assert.Equal("Assessment started", entry.Details.GetValueOrDefault("actionDisplayName"));

        // Assignee, payment date and SLA clock are untouched.
        Assert.Equal(assignedToId, persisted.AssignedToId);
        Assert.Equal("2026-08-20", persisted.Payload["paymentDate"].AsString);
        Assert.Equal(s_dulyMadeClockStart, persisted.SlaClock!.StartedAt);

        // The operator sees UPDATED: the status push for assessment-in-progress.
        var push = await pushed.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        Assert.Equal(("start-assessment", "assessment-in-progress", "Assessment started"), push);
    }

    [Theory]
    [InlineData("submitted", HttpStatusCode.BadRequest)]
    [InlineData("assessment-in-progress", HttpStatusCode.BadRequest)]
    [InlineData("awaiting-decision", HttpStatusCode.BadRequest)]
    [InlineData("queried", HttpStatusCode.BadRequest)]
    [InlineData("updated", HttpStatusCode.BadRequest)]
    [InlineData("approved", HttpStatusCode.Conflict)]
    [InlineData("withdrawn", HttpStatusCode.Conflict)]
    [InlineData("rejected", HttpStatusCode.Conflict)]
    public async Task Start_assessment_is_rejected_outside_duly_made(
        string stateId,
        HttpStatusCode expectedStatus)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, stateId, TenantClientId), cancellationToken);

        var response = await client.PostAsync(
            $"/work-items/{id}/actions/start-assessment",
            content: null,
            cancellationToken
        );

        Assert.Equal(expectedStatus, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(stateId, persisted!.StateId);
        Assert.Empty(persisted.AuditLog);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("bob-1")]
    public async Task Payment_received_is_rejected_as_an_unknown_action_and_changes_nothing(
        string? assignedToId)
    {
        // RA-595 ("remove" step): v16 no longer declares payment-received, so a
        // stale page or hand-crafted request can't bring the step back.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildDulyMade(id, TenantClientId, assignedToId), cancellationToken);
        var before = await factory.Persistence.GetByIdAsync(id, cancellationToken);

        var response = await client.PostAsync(
            $"/work-items/{id}/actions/payment-received",
            content: null,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.Contains("'payment-received' is not declared", body);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(before!.ToBsonDocument(), persisted!.ToBsonDocument());
    }

    [Fact]
    public async Task Payment_received_is_rejected_on_a_migrated_case_and_start_assessment_still_works()
    {
        // RA-595 ("remove" step): an existing v15 case still carries
        // payment-received in its stored rules until the v16 migration runs.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var item = BuildDulyMade(id, TenantClientId, "bob-1");
        item.TemplateVersion = "v15";
        item.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v15",
            States = item.TemplateSnapshot!.States,
            Transitions = item.TemplateSnapshot.Transitions
                .Append(new WorkItemTransition(
                    "payment-received", "Payment received", "duly-made", "assessment-in-progress"))
                .ToList(),
        };
        await factory.SeedAsync(item, cancellationToken);
        await new ReAccreditationRemovePaymentReceivedSnapshotMigration(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ReAccreditationRemovePaymentReceivedSnapshotMigration>.Instance
        ).ApplyAsync(factory.Persistence, cancellationToken);
        var migrated = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("v16", migrated!.TemplateVersion);

        var rejected = await client.PostAsync(
            $"/work-items/{id}/actions/payment-received", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains(
            "'payment-received' is not declared",
            await rejected.Content.ReadAsStringAsync(cancellationToken));
        var unchanged = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(migrated.ToBsonDocument(), unchanged!.ToBsonDocument());

        var started = await client.PostAsync(
            $"/work-items/{id}/actions/start-assessment", content: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("assessment-in-progress", persisted!.StateId);
        Assert.Equal("bob-1", persisted.AssignedToId);
    }

    [Fact]
    public async Task A_decision_on_a_duly_made_case_is_still_refused_and_changes_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildDulyMade(id, TenantClientId, "bob-1"), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "approved" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("duly-made", persisted!.StateId);
        Assert.Equal("bob-1", persisted.AssignedToId);
        Assert.Empty(persisted.AuditLog);
    }

    [Fact]
    public async Task The_sla_extend_action_on_a_duly_made_case_is_still_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildDulyMade(id, TenantClientId), cancellationToken);

        var response = await client.PostAsync(
            $"/work-items/{id}/actions/sla-extend",
            content: null,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("duly-made", persisted!.StateId);
        Assert.Empty(persisted.AuditLog);

        // Positive control: the same case lists start-assessment, not sla-extend.
        var projected = await client.GetFromJsonAsync<WorkItemResponse>(
            $"/work-items/{id}",
            cancellationToken
        );
        Assert.Contains(projected!.AvailableActions, a => a.ActionId == "start-assessment");
        Assert.DoesNotContain(projected.AvailableActions, a => a.ActionId == "sla-extend");
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

    // -------------------- RA-527: bespoke approve endpoint removed --------------------

    /// <summary>
    /// RA-527: the RA-132 bespoke <c>POST …/re-accreditation/{id}/approve</c>
    /// route is gone. Since RA-410 every determination goes through
    /// <c>/decision</c>; a route that approved directly would bypass the
    /// determination flow. The item must be left untouched.
    /// </summary>
    [Fact]
    public async Task Approve_route_no_longer_exists()
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

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("awaiting-decision", persisted!.StateId);
    }

    /// <summary>
    /// RA-475: the AccreditationNumberUnavailable → 500 mapping via the single-call
    /// /decision path (LogDecision's own switch), the only route that reaches the
    /// approval service.
    /// </summary>
    [Fact]
    public async Task Decision_returns_internal_server_error_when_the_accreditation_number_adapter_fails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var failingNumberAdapter = Substitute.For<IAccreditationNumberAdapter>();
        failingNumberAdapter
            .GenerateOrUpdateAccreditationNumberAsync(
                Arg.Any<AccreditationNumberRequest>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(AccreditationNumberResult.Failure("backend unreachable"));
        var pushAdapter = DecisionPushAdapterReturning(OperatorBackendPushResult.Success());
        await using var factory = new ReAccreditationFactory(
            _fixture,
            pushAdapter: pushAdapter,
            numberAdapter: failingNumberAdapter
        );
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAssessmentInProgress(id, TenantClientId), cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "approved" },
            cancellationToken
        );

        // AccreditationNumberUnavailable: nothing changes — the item stays in
        // assessment with no audit entry, and the operator journey is never
        // told about a decision that did not happen.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("assessment-in-progress", persisted!.StateId);
        Assert.False(persisted.Payload.Contains("accreditationId"));
        Assert.Empty(persisted.AuditLog);
        await pushAdapter
            .DidNotReceiveWithAnyArgs()
            .PushDecisionStatusChangedAsync(
                default,
                default,
                default!,
                default!,
                default!,
                default!,
                default!,
                default,
                cancellationToken
            );
    }

    // --------------------------- RA-410: single-call decision ---------------------------

    /// <summary>
    /// RA-527 step 1: one call carries an application from
    /// <c>assessment-in-progress</c>, through the <c>awaiting-decision</c> hop,
    /// to the non-terminal <c>approved-pending-contact</c> state. The number
    /// is minted and stamped with its start date and year, and the
    /// determination is audited — but the outcome is not applied: nothing is
    /// pushed to the operator journey, the SLA clock keeps running, no
    /// publishing job is queued and no Decision email is sent.
    /// </summary>
    [Fact]
    public async Task Decision_approved_records_step_one_and_applies_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pushAdapter = DecisionPushAdapterReturning(OperatorBackendPushResult.Success());
        var numberAdapter = Substitute.For<IAccreditationNumberAdapter>();
        numberAdapter
            .GenerateOrUpdateAccreditationNumberAsync(
                Arg.Any<AccreditationNumberRequest>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(AccreditationNumberResult.Success("A25ER5000270036WO"));
        var notifyClient = Substitute.For<INotifyClient>();
        var backgroundTaskQueue = Substitute.For<IBackgroundTaskQueue>();
        await using var factory = new ReAccreditationFactory(
            _fixture,
            pushAdapter: pushAdapter,
            numberAdapter: numberAdapter,
            notifyClient: notifyClient,
            backgroundTaskQueue: backgroundTaskQueue
        );
        using var client = factory.CreateClient();
        // Positive control: the email hook is live, so "no email" below means
        // it chose not to send one.
        Assert.Contains(
            factory.Services.GetServices<IWorkItemPostActionHook>(),
            h => h is ReAccreditationNotificationHook
        );

        var id = Guid.NewGuid();
        var seeded = BuildAssessmentInProgress(id, TenantClientId);
        seeded.Payload["operatorEmail"] = "operator@example.com";
        await factory.SeedAsync(seeded, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "approved" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("approved-pending-contact", body.GetProperty("stateId").GetString());

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("approved-pending-contact", persisted!.StateId);

        // The number is minted once and stamped, ready to show the regulator.
        await numberAdapter
            .Received(1)
            .GenerateOrUpdateAccreditationNumberAsync(
                Arg.Any<AccreditationNumberRequest>(),
                Arg.Any<CancellationToken>()
            );
        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(persisted.Payload);
        Assert.Equal("A25ER5000270036WO", payload.AccreditationId);
        // The start date and year wait for step 2 (RA-527 task 007).
        Assert.Null(payload.AccreditationStartDate);
        Assert.Null(payload.AccreditationYear);

        // The determination is in the history, made by the caller.
        var determination = Assert.Single(persisted.AuditLog, a => a.Action == "determination-made");
        Assert.Equal("Determination made: Approved", determination.ActionDisplayName);
        Assert.Equal(DefaultUserId, determination.CreatedBy);
        var applied = persisted
            .AuditLog.Where(e => e.Action == "action-applied")
            .Select(e => e.Details.GetValueOrDefault("actionId"))
            .ToList();
        Assert.Equal(["submit-for-decision", "make-determination-approved"], applied);

        // Nothing is applied: no push, SLA still running, no publishing, no email.
        await pushAdapter
            .DidNotReceiveWithAnyArgs()
            .PushDecisionStatusChangedAsync(
                default,
                default,
                default!,
                default!,
                default!,
                default!,
                default!,
                default,
                cancellationToken
            );
        await pushAdapter
            .DidNotReceiveWithAnyArgs()
            .PushStatusChangedAsync(
                default,
                default,
                default!,
                default!,
                default!,
                default!,
                default!,
                default,
                cancellationToken
            );
        Assert.Null(payload.SlaClock?.StoppedAt);
        Assert.DoesNotContain(
            persisted.AuditLog,
            a => a.Action is "sla-clock-stopped" or "accreditation-issued" or "publishing-enqueued"
                || a.Action.StartsWith("status-push-", StringComparison.Ordinal)
        );
        await backgroundTaskQueue.DidNotReceiveWithAnyArgs().QueueAsync(default!, cancellationToken);
        await notifyClient
            .DidNotReceiveWithAnyArgs()
            .SendEmailAsync(default!, default!, default!, default!, cancellationToken: cancellationToken);

        // Query, withdraw, due-date change and re-determination are all gone.
        var projected = await client.GetFromJsonAsync<JsonElement>(
            $"/work-items/{id}",
            cancellationToken
        );
        Assert.Equal("approved-pending-contact", projected.GetProperty("stateId").GetString());
        Assert.Empty(projected.GetProperty("availableActions").EnumerateArray());
    }

    /// <summary>
    /// RA-527: step 1 never talks to the operator journey, so an approval
    /// still lands while it is down.
    /// </summary>
    [Fact]
    public async Task Decision_approved_never_touches_the_operator_journey_even_when_it_is_down()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(
            _fixture,
            pushAdapter: DecisionPushAdapterReturning(
                OperatorBackendPushResult.Failure("operator journey unreachable")
            )
        );
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
        Assert.Equal("approved-pending-contact", persisted!.StateId);
        Assert.DoesNotContain(persisted.AuditLog, a => a.Action == "status-push-failed");
    }

    /// <summary>
    /// RA-527 step 1 of a refusal: the determination is recorded and the item
    /// moves to the non-terminal rejected-pending-contact ("Refused") state.
    /// No number is minted and nothing is applied: no operator-journey push,
    /// the SLA clock still running, no publishing and no email. Only
    /// reassign/unassign and confirm-contact remain (none is a projected
    /// generic action).
    /// </summary>
    [Fact]
    public async Task Decision_refused_records_step_one_and_applies_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pushAdapter = DecisionPushAdapterReturning(OperatorBackendPushResult.Success());
        var numberAdapter = Substitute.For<IAccreditationNumberAdapter>();
        var notifyClient = Substitute.For<INotifyClient>();
        var backgroundTaskQueue = Substitute.For<IBackgroundTaskQueue>();
        await using var factory = new ReAccreditationFactory(
            _fixture,
            pushAdapter: pushAdapter,
            numberAdapter: numberAdapter,
            notifyClient: notifyClient,
            backgroundTaskQueue: backgroundTaskQueue
        );
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var seeded = BuildAssessmentInProgress(id, TenantClientId);
        seeded.Payload["operatorEmail"] = "operator@example.com";
        await factory.SeedAsync(seeded, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "rejected" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("rejected-pending-contact", body.GetProperty("stateId").GetString());

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("rejected-pending-contact", persisted!.StateId);

        // No number is minted for a refusal.
        await numberAdapter
            .DidNotReceiveWithAnyArgs()
            .GenerateOrUpdateAccreditationNumberAsync(default!, cancellationToken);
        Assert.False(persisted.Payload.Contains("accreditationId"));

        // The determination is in the history, made by the caller.
        var determination = Assert.Single(persisted.AuditLog, a => a.Action == "determination-made");
        Assert.Equal("Determination made: Refused", determination.ActionDisplayName);
        Assert.Equal(DefaultUserId, determination.CreatedBy);
        var applied = persisted
            .AuditLog.Where(e => e.Action == "action-applied")
            .Select(e => e.Details.GetValueOrDefault("actionId"))
            .ToList();
        Assert.Equal(["submit-for-decision", "make-determination-rejected"], applied);

        // Nothing is applied: no push, SLA still running, no publishing, no email.
        Assert.Empty(pushAdapter.ReceivedCalls());
        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(persisted.Payload);
        Assert.Null(payload.SlaClock?.StoppedAt);
        Assert.DoesNotContain(
            persisted.AuditLog,
            a => a.Action is "sla-clock-stopped" or "applicant-contacted" or "publishing-enqueued"
                || a.Action.StartsWith("status-push-", StringComparison.Ordinal)
        );
        await backgroundTaskQueue.DidNotReceiveWithAnyArgs().QueueAsync(default!, cancellationToken);
        await notifyClient
            .DidNotReceiveWithAnyArgs()
            .SendEmailAsync(default!, default!, default!, default!, cancellationToken: cancellationToken);

        // Query, withdraw, due-date change and re-determination are all gone.
        var projected = await client.GetFromJsonAsync<JsonElement>(
            $"/work-items/{id}",
            cancellationToken
        );
        Assert.Equal("rejected-pending-contact", projected.GetProperty("stateId").GetString());
        Assert.Empty(projected.GetProperty("availableActions").EnumerateArray());
    }

    /// <summary>
    /// RA-527: step 1 of a refusal never talks to the operator journey, so it
    /// still lands while the operator journey is down.
    /// </summary>
    [Fact]
    public async Task Decision_refused_never_touches_the_operator_journey_even_when_it_is_down()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(
            _fixture,
            pushAdapter: DecisionPushAdapterReturning(
                OperatorBackendPushResult.Failure("operator journey unreachable")
            )
        );
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
        Assert.Equal("rejected-pending-contact", persisted!.StateId);
        Assert.DoesNotContain(persisted.AuditLog, a => a.Action == "status-push-failed");
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
        Assert.Equal("approved-pending-contact", persisted!.StateId);
    }

    /// <summary>
    /// RA-475 regression: a REAL application's <c>operatorOrganisationId</c>
    /// is the ReEx organisation id — a GUID — never a number. The seeded
    /// fixtures and every other test in this file use a numeric id
    /// ("500027"), which is why RA-448 phase 2's <c>int.TryParse</c> gate in
    /// ReAccreditationApprovalService went unnoticed: on dev it fails for
    /// every genuinely-submitted application, the failure surfaces as
    /// InvalidTransition -> 409, and management-fe renders that as "This
    /// application has changed since you opened it", which no amount of
    /// refreshing can clear.
    /// </summary>
    [Fact]
    public async Task Decision_approves_when_the_operator_organisation_id_is_a_reex_guid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var workItem = BuildAssessmentInProgress(id, TenantClientId);
        workItem.Payload["operatorOrganisationId"] = "c14854d9-e20a-41b3-87d5-a1fd4bbe2153";
        workItem.Payload["registrationNumber"] = "R25SR500000912AL";
        await factory.SeedAsync(workItem, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "approved" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("approved-pending-contact", persisted!.StateId);
    }

    /// <summary>
    /// RA-527: Approved again on an item already approved at step 1 succeeds,
    /// flagged as a replay, with no new side effects: no second mint, no
    /// new audit entries, the same number.
    /// </summary>
    [Fact]
    public async Task Decision_replay_is_idempotent_and_flags_the_replay()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var numberAdapter = Substitute.For<IAccreditationNumberAdapter>();
        numberAdapter
            .GenerateOrUpdateAccreditationNumberAsync(
                Arg.Any<AccreditationNumberRequest>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(AccreditationNumberResult.Success("A25ER5000270036WO"));
        await using var factory = new ReAccreditationFactory(_fixture, numberAdapter: numberAdapter);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAssessmentInProgress(id, TenantClientId), cancellationToken);

        var first = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "approved" },
            cancellationToken
        );
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var afterFirst = (await factory.Persistence.GetByIdAsync(id, cancellationToken))!;

        var replay = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "approved" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.Contains(WorkItemEndpoints.IdempotentReplayHeader));
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("approved-pending-contact", persisted!.StateId);
        Assert.Equal(
            afterFirst.Payload.GetValue("accreditationId").AsString,
            persisted.Payload.GetValue("accreditationId").AsString
        );
        Assert.Equal(afterFirst.AuditLog.Count, persisted.AuditLog.Count);
        Assert.Equal(afterFirst.Version, persisted.Version);
        await numberAdapter
            .Received(1)
            .GenerateOrUpdateAccreditationNumberAsync(
                Arg.Any<AccreditationNumberRequest>(),
                Arg.Any<CancellationToken>()
            );
    }

    /// <summary>
    /// The opposite outcome on a decided application is a conflict, never
    /// last-write-wins: reporting success would tell a caseworker their
    /// refusal landed on an application that is in fact approved. RA-527: the
    /// step 1 determination is already final, so this holds while the
    /// approval is pending contact.
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
        var before = await factory.Persistence.GetByIdAsync(id, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "rejected" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("approved-pending-contact", persisted!.StateId);
        Assert.Equal(before!.ToBsonDocument(), persisted.ToBsonDocument());
    }

    /// <summary>
    /// RA-527 task 007 (as on main): the OTHER outcome on a Granted or Refused
    /// application, or any outcome on a withdrawn one, is a conflict. Nothing
    /// is minted or written.
    /// </summary>
    [Theory]
    [InlineData("approved", "rejected")]
    [InlineData("rejected", "approved")]
    [InlineData("withdrawn", "approved")]
    [InlineData("withdrawn", "rejected")]
    public async Task Decision_with_the_other_outcome_on_a_closed_application_is_refused(
        string stateId,
        string outcome
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var numberAdapter = Substitute.For<IAccreditationNumberAdapter>();
        await using var factory = new ReAccreditationFactory(_fixture, numberAdapter: numberAdapter);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, stateId, TenantClientId), cancellationToken);
        var before = await factory.Persistence.GetByIdAsync(id, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(before!.ToBsonDocument(), persisted!.ToBsonDocument());
        Assert.Empty(numberAdapter.ReceivedCalls());
    }

    /// <summary>
    /// RA-527 task 007 (user decision: every response code matches main): the
    /// SAME outcome on an application already at its own final state is an
    /// idempotent replay — 200 with the replay header, the item unchanged and
    /// still final (nothing moves back to step 1), nothing minted, pushed,
    /// written or emailed.
    /// </summary>
    [Theory]
    [InlineData("approved", "approved")]
    [InlineData("rejected", "rejected")]
    public async Task Decision_with_the_same_outcome_on_a_final_application_is_an_idempotent_replay(
        string stateId,
        string outcome
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var numberAdapter = Substitute.For<IAccreditationNumberAdapter>();
        var pushAdapter = DecisionPushAdapterReturning(OperatorBackendPushResult.Success());
        var notifyClient = Substitute.For<INotifyClient>();
        await using var factory = new ReAccreditationFactory(
            _fixture,
            pushAdapter: pushAdapter,
            numberAdapter: numberAdapter,
            notifyClient: notifyClient
        );
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var seeded = BuildInState(id, stateId, TenantClientId);
        if (stateId == "approved")
        {
            seeded.Payload["accreditationId"] = StepOneAccreditationId;
        }
        await factory.SeedAsync(seeded, cancellationToken);
        var before = await factory.Persistence.GetByIdAsync(id, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains(WorkItemEndpoints.IdempotentReplayHeader));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal(stateId, body.GetProperty("stateId").GetString());
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(before!.ToBsonDocument(), persisted!.ToBsonDocument());
        Assert.Empty(numberAdapter.ReceivedCalls());
        Assert.Empty(pushAdapter.ReceivedCalls());
        Assert.Empty(notifyClient.ReceivedCalls());
    }

    /// <summary>
    /// RA-527 task 007 (restored from main's single-step approve): an
    /// application not yet approved that already carries a real-format
    /// accreditation id is a 409 "Manual investigation required" — nothing is
    /// minted and nothing is written, not even the submit-for-decision hop.
    /// </summary>
    [Fact]
    public async Task Decision_approved_on_an_item_already_carrying_an_accreditation_id_is_refused_before_the_mint()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var numberAdapter = Substitute.For<IAccreditationNumberAdapter>();
        await using var factory = new ReAccreditationFactory(_fixture, numberAdapter: numberAdapter);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var seeded = BuildAssessmentInProgress(id, TenantClientId);
        seeded.Payload["accreditationId"] = StepOneAccreditationId;
        await factory.SeedAsync(seeded, cancellationToken);
        var before = await factory.Persistence.GetByIdAsync(id, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "approved" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Contains("Manual investigation required", problem!.Detail);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(before!.ToBsonDocument(), persisted!.ToBsonDocument());
        Assert.Empty(numberAdapter.ReceivedCalls());
    }

    /// <summary>
    /// RA-527: the step 1 transition is not caller-invocable, so the generic
    /// action route cannot move an item into approved-pending-contact without
    /// a minted number.
    /// </summary>
    [Fact]
    public async Task Make_determination_approved_is_rejected_on_the_generic_action_route()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildAwaitingDecision(id, TenantClientId), cancellationToken);

        var response = await client.PostAsync(
            $"/work-items/{id}/actions/make-determination-approved",
            content: null,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("awaiting-decision", persisted!.StateId);
        Assert.Empty(persisted.AuditLog);
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
    /// can still post <c>reject</c>. Only this server-side check stops a
    /// caseworker refusing an application without ever seeing the decision
    /// page, so it must refuse before any transition is applied. RA-527: the
    /// transition is gone from v17 altogether, so it is now an unknown action.
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
        Assert.DoesNotContain(pending!.AvailableActions, a => a.ActionId == "submit-for-decision");
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

    // -------------------- RA-527: confirm applicant contact (step 2) --------------------

    private const string StepOneAccreditationId = "A25ER5000270036WO";

    /// <summary>
    /// Takes a freshly seeded <c>assessment-in-progress</c> application through
    /// step 1 (Approved) on the real /decision endpoint, so step 2 starts from
    /// exactly what production leaves behind.
    /// </summary>
    private static async Task<WorkItem> SeedApprovedPendingContactAsync(
        ReAccreditationFactory factory,
        HttpClient client,
        Guid id,
        CancellationToken cancellationToken
    )
    {
        var seeded = BuildAssessmentInProgress(id, TenantClientId);
        seeded.Payload["operatorEmail"] = "operator@example.com";
        await factory.SeedAsync(seeded, cancellationToken);

        var stepOne = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "approved" },
            cancellationToken
        );
        Assert.Equal(HttpStatusCode.OK, stepOne.StatusCode);

        var pending = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("approved-pending-contact", pending!.StateId);
        return pending;
    }

    private static Task<HttpResponseMessage> PostConfirmContactAsync(
        HttpClient client,
        Guid id,
        CancellationToken cancellationToken
    ) =>
        client.PostAsync(
            $"/work-items/re-accreditation/{id}/confirm-contact",
            content: null,
            cancellationToken
        );

    /// <summary>
    /// RA-527 step 2: confirming applicant contact applies the approval. The
    /// operator journey is told first (to-state <c>approved</c>, from-state
    /// <c>approved-pending-contact</c>), then one write moves the item to
    /// Granted with the SLA clock stopped at the push's own timestamp, the
    /// "Applicant contacted" audit entry, the accreditation-issued entry for
    /// the step 1 number (never re-minted) and a publishing job. The Decision
    /// email follows.
    /// </summary>
    [Fact]
    public async Task Confirm_contact_applies_the_approval()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pushAdapter = DecisionPushAdapterReturning(OperatorBackendPushResult.Success());
        var numberAdapter = Substitute.For<IAccreditationNumberAdapter>();
        numberAdapter
            .GenerateOrUpdateAccreditationNumberAsync(
                Arg.Any<AccreditationNumberRequest>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(AccreditationNumberResult.Success(StepOneAccreditationId));
        var notifyClient = Substitute.For<INotifyClient>();
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new NotifySendResult(true, "notify-1", null));
        var backgroundTaskQueue = Substitute.For<IBackgroundTaskQueue>();
        await using var factory = new ReAccreditationFactory(
            _fixture,
            pushAdapter: pushAdapter,
            numberAdapter: numberAdapter,
            notifyClient: notifyClient,
            backgroundTaskQueue: backgroundTaskQueue
        );
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await SeedApprovedPendingContactAsync(factory, client, id, cancellationToken);

        var response = await PostConfirmContactAsync(client, id, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("approved", body.GetProperty("stateId").GetString());

        // The operator journey is told the final outcome, once.
        var push = Assert.Single(
            pushAdapter.ReceivedCalls(),
            c => c.GetMethodInfo().Name == nameof(IOperatorBackendPushAdapter.PushDecisionStatusChangedAsync)
        );
        var pushArgs = push.GetArguments();
        Assert.Equal(id, pushArgs[0]);
        Assert.Equal("approved-pending-contact", pushArgs[2]);
        Assert.Equal("approved", pushArgs[3]);
        Assert.Equal("Granted", pushArgs[4]);
        Assert.Equal(ReAccreditationType.ConfirmContactApprovedActionId, pushArgs[5]);
        var occurredAt = (DateTime)pushArgs[7]!;

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("approved", persisted!.StateId);

        // SLA clock stopped at the push's own timestamp; the step 1 number kept.
        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(persisted.Payload);
        Assert.Equal(occurredAt, payload.SlaClock?.StoppedAt?.UtcDateTime);
        Assert.Equal(StepOneAccreditationId, payload.AccreditationId);
        await numberAdapter
            .Received(1)
            .GenerateOrUpdateAccreditationNumberAsync(
                Arg.Any<AccreditationNumberRequest>(),
                Arg.Any<CancellationToken>()
            );

        // "Applicant contacted", made by the caller, plus the existing entries.
        var contacted = Assert.Single(persisted.AuditLog, a => a.Action == "applicant-contacted");
        Assert.Equal("Applicant contacted", contacted.ActionDisplayName);
        Assert.Equal(DefaultUserId, contacted.CreatedBy);
        Assert.Equal(DefaultUserName, contacted.CreatedByName);
        var applied = persisted
            .AuditLog.Where(e => e.Action == "action-applied")
            .Select(e => e.Details.GetValueOrDefault("actionId"))
            .ToList();
        Assert.Equal(
            [
                "submit-for-decision",
                "make-determination-approved",
                ReAccreditationType.ConfirmContactApprovedActionId,
            ],
            applied
        );
        Assert.Single(persisted.AuditLog, a => a.Action == "sla-clock-stopped");
        var issued = Assert.Single(persisted.AuditLog, a => a.Action == "accreditation-issued");
        Assert.Equal(StepOneAccreditationId, issued.Details.GetValueOrDefault("accreditationId"));
        // RA-527 task 007: the start date and year are stamped here, at step 2,
        // and reported on the accreditation-issued entry.
        Assert.NotNull(payload.AccreditationStartDate);
        Assert.NotNull(payload.AccreditationYear);
        Assert.Equal(
            payload.AccreditationStartDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            issued.Details.GetValueOrDefault("startDate")
        );
        Assert.Equal(
            payload.AccreditationYear!.Value.ToString(CultureInfo.InvariantCulture),
            issued.Details.GetValueOrDefault("accreditationYear")
        );
        Assert.Single(persisted.AuditLog, a => a.Action == "status-push-sent");

        // Publishing queued, and the Decision email sent to the operator.
        await backgroundTaskQueue.ReceivedWithAnyArgs(1).QueueAsync(default!, cancellationToken);
        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "Decision",
                "operator@example.com",
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            );
    }

    /// <summary>
    /// RA-527 step 2: the operator-journey push is a hard pre-commit gate.
    /// When it cannot be delivered, confirming contact is abandoned with a
    /// generic 500 and nothing applied: the item is still pending contact,
    /// the SLA clock still running, no "Applicant contacted" or other outcome
    /// entry, no publishing job and no email — only the regulator-visible
    /// status-push-failed entry, as for a refusal. Once the operator journey
    /// answers again the identical call applies the approval.
    /// </summary>
    [Fact]
    public async Task Confirm_contact_applies_nothing_when_the_operator_journey_push_fails_and_succeeds_on_retry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var backgroundTaskQueue = Substitute.For<IBackgroundTaskQueue>();
        await using var factory = new ReAccreditationFactory(
            _fixture,
            pushAdapter: DecisionPushAdapterReturning(
                OperatorBackendPushResult.Failure("operator journey unreachable"),
                OperatorBackendPushResult.Success()
            ),
            notifyClient: notifyClient,
            backgroundTaskQueue: backgroundTaskQueue
        );
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var pending = await SeedApprovedPendingContactAsync(factory, client, id, cancellationToken);

        var failed = await PostConfirmContactAsync(client, id, cancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        var unchanged = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("approved-pending-contact", unchanged!.StateId);
        var unchangedPayload = BsonSerializer.Deserialize<ReAccreditationPayload>(unchanged.Payload);
        Assert.Null(unchangedPayload.SlaClock?.StoppedAt);
        // No start date or year either (RA-527 task 007): the payload is untouched.
        Assert.Equal(pending.Payload, unchanged.Payload);
        var added = Assert.Single(unchanged.AuditLog.Skip(pending.AuditLog.Count));
        Assert.Equal("status-push-failed", added.Action);
        await backgroundTaskQueue.DidNotReceiveWithAnyArgs().QueueAsync(default!, cancellationToken);
        await notifyClient
            .DidNotReceiveWithAnyArgs()
            .SendEmailAsync(default!, default!, default!, default!, cancellationToken: cancellationToken);

        var retried = await PostConfirmContactAsync(client, id, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("approved", persisted!.StateId);
        Assert.Single(persisted.AuditLog, a => a.Action == "applicant-contacted");
    }

    /// <summary>
    /// RA-527: confirming again once the approval is applied (a double-click,
    /// or a retried request) succeeds, flagged as a replay, with no side
    /// effects: no second push, no new audit entries, no second write.
    /// </summary>
    [Fact]
    public async Task Confirm_contact_replay_on_a_granted_application_is_idempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pushAdapter = DecisionPushAdapterReturning(OperatorBackendPushResult.Success());
        // Substituted so the publishing job's own audit entry cannot land
        // between the two reads below.
        await using var factory = new ReAccreditationFactory(
            _fixture,
            pushAdapter: pushAdapter,
            backgroundTaskQueue: Substitute.For<IBackgroundTaskQueue>()
        );
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await SeedApprovedPendingContactAsync(factory, client, id, cancellationToken);
        var first = await PostConfirmContactAsync(client, id, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var afterFirst = (await factory.Persistence.GetByIdAsync(id, cancellationToken))!;

        var replay = await PostConfirmContactAsync(client, id, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.Contains(WorkItemEndpoints.IdempotentReplayHeader));
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("approved", persisted!.StateId);
        Assert.Equal(afterFirst.AuditLog.Count, persisted.AuditLog.Count);
        Assert.Equal(afterFirst.Version, persisted.Version);
        Assert.Single(
            pushAdapter.ReceivedCalls(),
            c => c.GetMethodInfo().Name == nameof(IOperatorBackendPushAdapter.PushDecisionStatusChangedAsync)
        );
    }

    /// <summary>
    /// RA-527: contact can only be confirmed for an application pending
    /// contact. Anything else — not yet determined, queried or withdrawn — is
    /// a 409, and nothing is pushed or written. (Granted and Refused are
    /// replays.)
    /// </summary>
    [Theory]
    [InlineData("submitted")]
    [InlineData("assessment-in-progress")]
    [InlineData("awaiting-decision")]
    [InlineData("queried")]
    [InlineData("withdrawn")]
    public async Task Confirm_contact_is_refused_outside_a_pending_contact_state(string stateId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pushAdapter = DecisionPushAdapterReturning(OperatorBackendPushResult.Success());
        await using var factory = new ReAccreditationFactory(_fixture, pushAdapter: pushAdapter);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, stateId, TenantClientId), cancellationToken);
        var before = await factory.Persistence.GetByIdAsync(id, cancellationToken);

        var response = await PostConfirmContactAsync(client, id, cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.Equal(ReAccreditationEndpoints.ConfirmContactProblemTitle, problem!.Title);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(before!.ToBsonDocument(), persisted!.ToBsonDocument());
        Assert.Empty(pushAdapter.ReceivedCalls());
    }

    /// <summary>
    /// RA-527 task 007: every rule that can refuse the step 2 write runs
    /// before the push, including payload readability. A corrupt payload is a
    /// 409 with nothing pushed and nothing written, for both outcomes.
    /// </summary>
    [Theory]
    [InlineData("approved-pending-contact")]
    [InlineData("rejected-pending-contact")]
    public async Task Confirm_contact_on_a_corrupt_payload_is_refused_before_the_push(string stateId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pushAdapter = DecisionPushAdapterReturning(OperatorBackendPushResult.Success());
        await using var factory = new ReAccreditationFactory(_fixture, pushAdapter: pushAdapter);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var seeded = BuildInState(id, stateId, TenantClientId);
        seeded.Payload["accreditationId"] = StepOneAccreditationId;
        seeded.Payload["accreditationStartDate"] = "not-a-date";
        await factory.SeedAsync(seeded, cancellationToken);
        var before = await factory.Persistence.GetByIdAsync(id, cancellationToken);

        var response = await PostConfirmContactAsync(client, id, cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(before!.ToBsonDocument(), persisted!.ToBsonDocument());
        Assert.Empty(pushAdapter.ReceivedCalls());
    }

    [Fact]
    public async Task Confirm_contact_returns_not_found_for_a_missing_work_item()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var response = await PostConfirmContactAsync(client, Guid.NewGuid(), cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Confirm_contact_returns_bad_request_with_wrong_work_item_type_error_code_for_another_type()
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
                StateId = "approved-pending-contact",
                SubmittedBy = TenantClientId,
            },
            cancellationToken
        );

        var response = await PostConfirmContactAsync(client, id, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("wrong-work-item-type", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Confirm_contact_returns_unauthorized_without_a_forwarded_user_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pushAdapter = DecisionPushAdapterReturning(OperatorBackendPushResult.Success());
        await using var factory = new ReAccreditationFactory(
            _fixture,
            userId: null,
            pushAdapter: pushAdapter
        );
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildInState(id, "approved-pending-contact", TenantClientId),
            cancellationToken
        );

        var response = await PostConfirmContactAsync(client, id, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(pushAdapter.ReceivedCalls());
    }

    /// <summary>
    /// RA-527: the confirm-contact transition is not caller-invocable, so the
    /// generic action route cannot apply the outcome without the push, the
    /// SLA stop, publishing and the email.
    /// </summary>
    [Fact]
    public async Task Confirm_contact_approved_is_rejected_on_the_generic_action_route()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await SeedApprovedPendingContactAsync(factory, client, id, cancellationToken);

        var response = await client.PostAsync(
            $"/work-items/{id}/actions/{ReAccreditationType.ConfirmContactApprovedActionId}",
            content: null,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("approved-pending-contact", persisted!.StateId);
    }

    // -------------------- RA-527: a refusal, step 1 and step 2 --------------------

    /// <summary>
    /// Takes a freshly seeded <c>assessment-in-progress</c> application through
    /// step 1 (Refused) on the real /decision endpoint, so step 2 starts from
    /// exactly what production leaves behind.
    /// </summary>
    private static async Task<WorkItem> SeedRejectedPendingContactAsync(
        ReAccreditationFactory factory,
        HttpClient client,
        Guid id,
        CancellationToken cancellationToken
    )
    {
        var seeded = BuildAssessmentInProgress(id, TenantClientId);
        seeded.Payload["operatorEmail"] = "operator@example.com";
        await factory.SeedAsync(seeded, cancellationToken);

        var stepOne = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "rejected" },
            cancellationToken
        );
        Assert.Equal(HttpStatusCode.OK, stepOne.StatusCode);

        var pending = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("rejected-pending-contact", pending!.StateId);
        return pending;
    }

    /// <summary>
    /// RA-527 step 2 of a refusal: confirming applicant contact applies it.
    /// The operator journey is told first (to-state <c>rejected</c>,
    /// from-state <c>rejected-pending-contact</c>), then one write moves the
    /// item to Refused with the "Applicant contacted" audit entry. As the old
    /// single-step refuse, the SLA clock is NOT stopped (RA-527 task 007). No
    /// email (RA-211), nothing published and no accreditation issued.
    /// </summary>
    [Fact]
    public async Task Confirm_contact_applies_the_refusal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pushAdapter = DecisionPushAdapterReturning(OperatorBackendPushResult.Success());
        var notifyClient = Substitute.For<INotifyClient>();
        var backgroundTaskQueue = Substitute.For<IBackgroundTaskQueue>();
        await using var factory = new ReAccreditationFactory(
            _fixture,
            pushAdapter: pushAdapter,
            notifyClient: notifyClient,
            backgroundTaskQueue: backgroundTaskQueue
        );
        using var client = factory.CreateClient();
        // Positive control: the email hook is live, so "no email" below means
        // it chose not to send one.
        Assert.Contains(
            factory.Services.GetServices<IWorkItemPostActionHook>(),
            h => h is ReAccreditationNotificationHook
        );

        var id = Guid.NewGuid();
        await SeedRejectedPendingContactAsync(factory, client, id, cancellationToken);

        var response = await PostConfirmContactAsync(client, id, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("rejected", body.GetProperty("stateId").GetString());

        // The operator journey is told the final outcome, once.
        var push = Assert.Single(
            pushAdapter.ReceivedCalls(),
            c => c.GetMethodInfo().Name == nameof(IOperatorBackendPushAdapter.PushDecisionStatusChangedAsync)
        );
        var pushArgs = push.GetArguments();
        Assert.Equal(id, pushArgs[0]);
        Assert.Equal("rejected-pending-contact", pushArgs[2]);
        Assert.Equal("rejected", pushArgs[3]);
        Assert.Equal("Refused", pushArgs[4]);
        Assert.Equal(ReAccreditationType.ConfirmContactRejectedActionId, pushArgs[5]);
        Assert.NotEqual(default, (DateTime)pushArgs[7]!);

        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("rejected", persisted!.StateId);

        // The SLA clock is left as it was (RA-527 task 007); no number anywhere.
        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(persisted.Payload);
        Assert.Null(payload.SlaClock?.StoppedAt);
        Assert.Null(payload.AccreditationId);

        // "Applicant contacted", made by the caller.
        var contacted = Assert.Single(persisted.AuditLog, a => a.Action == "applicant-contacted");
        Assert.Equal("Applicant contacted", contacted.ActionDisplayName);
        Assert.Equal(DefaultUserId, contacted.CreatedBy);
        Assert.Equal(DefaultUserName, contacted.CreatedByName);
        var applied = persisted
            .AuditLog.Where(e => e.Action == "action-applied")
            .Select(e => e.Details.GetValueOrDefault("actionId"))
            .ToList();
        Assert.Equal(
            [
                "submit-for-decision",
                "make-determination-rejected",
                ReAccreditationType.ConfirmContactRejectedActionId,
            ],
            applied
        );
        // RA-527 task 007: as the old single-step refuse, the SLA clock is not stopped.
        Assert.DoesNotContain(persisted.AuditLog, a => a.Action == "sla-clock-stopped");
        Assert.Single(persisted.AuditLog, a => a.Action == "status-push-sent");
        Assert.DoesNotContain(persisted.AuditLog, a => a.Action == "accreditation-issued");

        // Nothing published and no email.
        await backgroundTaskQueue.DidNotReceiveWithAnyArgs().QueueAsync(default!, cancellationToken);
        await notifyClient
            .DidNotReceiveWithAnyArgs()
            .SendEmailAsync(default!, default!, default!, default!, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// RA-527 step 2 of a refusal: the operator-journey push is a hard
    /// pre-commit gate, as for an approval. When it cannot be delivered,
    /// confirming contact is abandoned with a generic 500 and nothing applied:
    /// still pending contact, the SLA clock still running, no outcome
    /// entries — only the regulator-visible status-push-failed entry. Once the
    /// operator journey answers again the identical call applies the refusal.
    /// (Carries over the RA-410 refusal push-failure tests, which replaced the
    /// deleted mgmt-tests ra-410-decision-operator-failure.e2e.js.)
    /// </summary>
    [Fact]
    public async Task Confirm_contact_applies_no_refusal_when_the_operator_journey_push_fails_and_succeeds_on_retry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(
            _fixture,
            pushAdapter: DecisionPushAdapterReturning(
                OperatorBackendPushResult.Failure("operator journey unreachable"),
                OperatorBackendPushResult.Success()
            )
        );
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var pending = await SeedRejectedPendingContactAsync(factory, client, id, cancellationToken);

        var failed = await PostConfirmContactAsync(client, id, cancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        var unchanged = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("rejected-pending-contact", unchanged!.StateId);
        var unchangedPayload = BsonSerializer.Deserialize<ReAccreditationPayload>(unchanged.Payload);
        Assert.Null(unchangedPayload.SlaClock?.StoppedAt);
        Assert.Equal(pending.Payload, unchanged.Payload);
        var added = Assert.Single(unchanged.AuditLog.Skip(pending.AuditLog.Count));
        Assert.Equal("status-push-failed", added.Action);

        var retried = await PostConfirmContactAsync(client, id, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("rejected", persisted!.StateId);
        Assert.Single(persisted.AuditLog, a => a.Action == "applicant-contacted");
    }

    /// <summary>
    /// RA-527: confirming again once the refusal is applied succeeds, flagged
    /// as a replay, with no side effects.
    /// </summary>
    [Fact]
    public async Task Confirm_contact_replay_on_a_refused_application_is_idempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pushAdapter = DecisionPushAdapterReturning(OperatorBackendPushResult.Success());
        await using var factory = new ReAccreditationFactory(_fixture, pushAdapter: pushAdapter);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await SeedRejectedPendingContactAsync(factory, client, id, cancellationToken);
        var first = await PostConfirmContactAsync(client, id, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var afterFirst = (await factory.Persistence.GetByIdAsync(id, cancellationToken))!;

        var replay = await PostConfirmContactAsync(client, id, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.Contains(WorkItemEndpoints.IdempotentReplayHeader));
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("rejected", persisted!.StateId);
        Assert.Equal(afterFirst.AuditLog.Count, persisted.AuditLog.Count);
        Assert.Equal(afterFirst.Version, persisted.Version);
        Assert.Single(
            pushAdapter.ReceivedCalls(),
            c => c.GetMethodInfo().Name == nameof(IOperatorBackendPushAdapter.PushDecisionStatusChangedAsync)
        );
    }

    /// <summary>
    /// RA-527: Refused again on an item already refused at step 1 succeeds,
    /// flagged as a replay, with no new audit entries.
    /// </summary>
    [Fact]
    public async Task Decision_refused_replay_is_idempotent_and_flags_the_replay()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var afterFirst = await SeedRejectedPendingContactAsync(factory, client, id, cancellationToken);

        var replay = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "rejected" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.Contains(WorkItemEndpoints.IdempotentReplayHeader));
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal("rejected-pending-contact", persisted!.StateId);
        Assert.Equal(afterFirst.AuditLog.Count, persisted.AuditLog.Count);
        Assert.Equal(afterFirst.Version, persisted.Version);
    }

    /// <summary>
    /// RA-527: Approved on an item already refused at step 1 is a conflict —
    /// the determination is final — and changes nothing.
    /// </summary>
    [Fact]
    public async Task Decision_approved_on_an_application_pending_refusal_is_refused_and_changes_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var numberAdapter = Substitute.For<IAccreditationNumberAdapter>();
        await using var factory = new ReAccreditationFactory(_fixture, numberAdapter: numberAdapter);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var before = await SeedRejectedPendingContactAsync(factory, client, id, cancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/work-items/re-accreditation/{id}/decision",
            new { outcome = "approved" },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(before.ToBsonDocument(), persisted!.ToBsonDocument());
        Assert.Empty(numberAdapter.ReceivedCalls());
    }

    /// <summary>
    /// RA-527: neither refusal transition is caller-invocable, so the generic
    /// action route can neither record a refusal nor apply one.
    /// </summary>
    [Theory]
    [InlineData("awaiting-decision", "make-determination-rejected")]
    [InlineData("rejected-pending-contact", "confirm-contact-rejected")]
    public async Task The_refusal_transitions_are_rejected_on_the_generic_action_route(
        string stateId,
        string actionId
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(_fixture);
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(BuildInState(id, stateId, TenantClientId), cancellationToken);

        var response = await client.PostAsync(
            $"/work-items/{id}/actions/{actionId}",
            content: null,
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var persisted = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.Equal(stateId, persisted!.StateId);
        Assert.Empty(persisted.AuditLog);
    }

    // -------------------- RA-469: Update recycling operations endpoint --------------------

    private const string RecyclingOperationsSiteId = "site-42";

    [Fact]
    public async Task RecyclingOperations_returns_ok_with_updated_site_json_for_standard_role_matching_nation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(
            _fixture,
            role: "standard",
            nation: "England"
        );
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildRecyclingOperationsCandidate(id, TenantClientId, "England"),
            cancellationToken
        );

        var response = await client.PatchAsJsonAsync(
            $"/work-items/re-accreditation/{id}/overseas-sites/{RecyclingOperationsSiteId}/recycling-operations",
            new { operationCodes = s_operationCodesR3AndR4 },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("site-1", body.GetProperty("id").GetString());

        await factory
            .RecyclingOperationsAdapter.Received(1)
            .UpdateRecyclingOperationsAsync(
                Arg.Is<OverseasSiteRecyclingOperationsRequest>(r =>
                    r.OrganisationId == "500027"
                    && r.ApplicationId == "APP-500027"
                    && r.SiteId == RecyclingOperationsSiteId
                    && r.OperationCodes.SequenceEqual(s_operationCodesR3AndR4)
                    && r.UserId == DefaultUserId
                    && r.UserName == DefaultUserName
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task RecyclingOperations_success_refreshes_the_stored_site_snapshot_and_stamps_audit_fields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(
            _fixture,
            role: "standard",
            nation: "England"
        );
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildRecyclingOperationsCandidateWithOverseasSites(id, TenantClientId, "England"),
            cancellationToken
        );

        var beforeCall = DateTime.UtcNow;
        var response = await client.PatchAsJsonAsync(
            $"/work-items/re-accreditation/{id}/overseas-sites/{RecyclingOperationsSiteId}/recycling-operations",
            new { operationCodes = s_operationCodesR3AndR4 },
            cancellationToken
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // AC13: the stored snapshot this repo's own case-review UI reads on
        // every subsequent page load must reflect the new codes
        // immediately, not just the response body the caller happened to
        // get back.
        var stored = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.NotNull(stored);
        var sites = stored!.Payload["overseasSites"].AsBsonDocument["sites"].AsBsonArray;
        var site = sites
            .Select(s => s.AsBsonDocument)
            .Single(s => s["siteId"].AsString == RecyclingOperationsSiteId);

        Assert.Equal(
            s_operationCodesR3AndR4,
            site["operationCodes"].AsBsonArray.Select(v => v.AsString).ToArray()
        );
        Assert.Equal(DefaultUserName, site["recyclingOperationsUpdatedBy"].AsString);
        var updatedAt = DateTime.Parse(
            site["recyclingOperationsUpdatedAt"].AsString,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind
        );
        Assert.True(updatedAt >= beforeCall && updatedAt <= DateTime.UtcNow);

        // A sibling site on the same work item must be untouched.
        var otherSite = sites
            .Select(s => s.AsBsonDocument)
            .Single(s => s["siteId"].AsString == "site-other");
        Assert.Equal(
            s_operationCodesR5,
            otherSite["operationCodes"].AsBsonArray.Select(v => v.AsString).ToArray()
        );
        Assert.False(otherSite.Contains("recyclingOperationsUpdatedBy"));
    }

    [Fact]
    public async Task RecyclingOperations_success_refreshes_the_stored_snapshot_when_siteId_is_a_real_int()
    {
        // The operator backend's OverseasSiteModel.SiteId is a C# int, and
        // ReAccreditationSeeder stores real/seeded overseasSites.sites[].siteId
        // as a BSON int, not a string — unlike every other test in this file,
        // which uses RecyclingOperationsSiteId ("site-42", a string) purely as
        // a test-fixture convenience. This proves the site lookup still works
        // against the actual on-the-wire shape, not just the fixture shape.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(
            _fixture,
            role: "standard",
            nation: "England"
        );
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var candidate = BuildRecyclingOperationsCandidate(id, TenantClientId, "England");
        candidate.Payload["overseasSites"] = new BsonDocument
        {
            ["sites"] = new BsonArray
            {
                new BsonDocument
                {
                    ["siteId"] = 1,
                    ["siteName"] = "Real-Shaped Site",
                    ["operationCodes"] = new BsonArray { "R5" },
                },
            },
        };
        await factory.SeedAsync(candidate, cancellationToken);

        var response = await client.PatchAsJsonAsync(
            $"/work-items/re-accreditation/{id}/overseas-sites/1/recycling-operations",
            new { operationCodes = s_operationCodesR3AndR4 },
            cancellationToken
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        Assert.NotNull(stored);
        var site = stored!
            .Payload["overseasSites"]
            .AsBsonDocument["sites"]
            .AsBsonArray.Select(s => s.AsBsonDocument)
            .Single(s => s["siteId"].AsInt32 == 1);

        Assert.Equal(
            s_operationCodesR3AndR4,
            site["operationCodes"].AsBsonArray.Select(v => v.AsString).ToArray()
        );
        // The lookup must also not have altered siteId's own BSON type.
        Assert.Equal(BsonType.Int32, site["siteId"].BsonType);
    }

    [Fact]
    public async Task RecyclingOperations_success_still_returns_ok_when_no_site_in_the_stored_snapshot_matches()
    {
        // The edit already succeeded on the operator backend (the system of
        // record) by the time RefreshStoredRecyclingOperationsAsync runs - if
        // this repo's own stored overseasSites.sites doesn't contain a
        // matching siteId (a legitimately possible drift: this snapshot is
        // only refreshed on a successful edit, so it can lag the operator
        // backend), the refresh must silently no-op rather than turn an
        // already-successful write into an error response.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(
            _fixture,
            role: "standard",
            nation: "England"
        );
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var candidate = BuildRecyclingOperationsCandidate(id, TenantClientId, "England");
        candidate.Payload["overseasSites"] = new BsonDocument
        {
            ["sites"] = new BsonArray
            {
                new BsonDocument
                {
                    ["siteId"] = 999,
                    ["siteName"] = "A Different Site",
                    ["operationCodes"] = new BsonArray { "R5" },
                },
            },
        };
        await factory.SeedAsync(candidate, cancellationToken);

        var response = await client.PatchAsJsonAsync(
            $"/work-items/re-accreditation/{id}/overseas-sites/{RecyclingOperationsSiteId}/recycling-operations",
            new { operationCodes = s_operationCodesR3 },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await factory.Persistence.GetByIdAsync(id, cancellationToken);
        var untouchedSite = stored!
            .Payload["overseasSites"]
            .AsBsonDocument["sites"]
            .AsBsonArray.Select(s => s.AsBsonDocument)
            .Single(s => s["siteId"].AsInt32 == 999);
        Assert.Equal(
            s_operationCodesR5,
            untouchedSite["operationCodes"].AsBsonArray.Select(v => v.AsString).ToArray()
        );
    }

    [Fact]
    public async Task RecyclingOperations_success_still_returns_ok_when_the_snapshot_refresh_write_fails()
    {
        // Same reasoning as the "no matching site" case above: the edit
        // already succeeded on the operator backend, so a failure refreshing
        // this repo's own local snapshot must be logged and swallowed, never
        // surfaced as an error on a write that genuinely went through.
        var cancellationToken = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await using var factory = new ReAccreditationFactory(
            _fixture,
            role: "standard",
            nation: "England",
            throwOnSetPayloadFieldWorkItemId: id
        );
        using var client = factory.CreateClient();

        await factory.SeedAsync(
            BuildRecyclingOperationsCandidateWithOverseasSites(id, TenantClientId, "England"),
            cancellationToken
        );

        var response = await client.PatchAsJsonAsync(
            $"/work-items/re-accreditation/{id}/overseas-sites/{RecyclingOperationsSiteId}/recycling-operations",
            new { operationCodes = s_operationCodesR3 },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RecyclingOperations_returns_forbidden_for_support_readonly_role()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(
            _fixture,
            role: "support-readonly",
            nation: "England"
        );
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildRecyclingOperationsCandidate(id, TenantClientId, "England"),
            cancellationToken
        );

        var response = await client.PatchAsJsonAsync(
            $"/work-items/re-accreditation/{id}/overseas-sites/{RecyclingOperationsSiteId}/recycling-operations",
            new { operationCodes = s_operationCodesR3 },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await factory
            .RecyclingOperationsAdapter.DidNotReceiveWithAnyArgs()
            .UpdateRecyclingOperationsAsync(default!, cancellationToken);
    }

    [Fact]
    public async Task RecyclingOperations_returns_forbidden_for_standard_role_with_mismatched_nation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(
            _fixture,
            role: "standard",
            nation: "Scotland"
        );
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildRecyclingOperationsCandidate(id, TenantClientId, "England"),
            cancellationToken
        );

        var response = await client.PatchAsJsonAsync(
            $"/work-items/re-accreditation/{id}/overseas-sites/{RecyclingOperationsSiteId}/recycling-operations",
            new { operationCodes = s_operationCodesR3 },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await factory
            .RecyclingOperationsAdapter.DidNotReceiveWithAnyArgs()
            .UpdateRecyclingOperationsAsync(default!, cancellationToken);
    }

    [Theory]
    [InlineData(null)] // missing user:role claim entirely
    [InlineData("")]
    public async Task RecyclingOperations_returns_forbidden_when_role_claim_is_missing(string? role)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(
            _fixture,
            role: role,
            nation: "England"
        );
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildRecyclingOperationsCandidate(id, TenantClientId, "England"),
            cancellationToken
        );

        var response = await client.PatchAsJsonAsync(
            $"/work-items/re-accreditation/{id}/overseas-sites/{RecyclingOperationsSiteId}/recycling-operations",
            new { operationCodes = s_operationCodesR3 },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RecyclingOperations_returns_forbidden_when_nation_claim_is_missing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(
            _fixture,
            role: "standard",
            nation: null
        );
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildRecyclingOperationsCandidate(id, TenantClientId, "England"),
            cancellationToken
        );

        var response = await client.PatchAsJsonAsync(
            $"/work-items/re-accreditation/{id}/overseas-sites/{RecyclingOperationsSiteId}/recycling-operations",
            new { operationCodes = s_operationCodesR3 },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await factory
            .RecyclingOperationsAdapter.DidNotReceiveWithAnyArgs()
            .UpdateRecyclingOperationsAsync(default!, cancellationToken);
    }

    /// <summary>
    /// Explicit fail-closed proof for a work item whose payload has no
    /// Nation set at all (e.g. a legacy/incomplete record) — this must
    /// NOT be treated as "no nation restriction applies" and pass every
    /// caller through. A missing/null resource-side Nation and a
    /// mismatched claim are the SAME 403 outcome as
    /// <see cref="RecyclingOperations_returns_forbidden_for_standard_role_with_mismatched_nation"/>.
    /// </summary>
    [Fact]
    public async Task RecyclingOperations_returns_forbidden_when_work_item_has_no_nation_set()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(
            _fixture,
            role: "standard",
            nation: "England"
        );
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        var type = new ReAccreditationType();
        await factory.SeedAsync(
            new WorkItem
            {
                Id = id,
                TypeId = ReAccreditationType.Id,
                StateId = "assessment-in-progress",
                SubmittedBy = TenantClientId,
                Payload = new BsonDocument
                {
                    ["operatorOrganisationId"] = "500027",
                    ["operatorApplicationId"] = "APP-500027",
                    // Deliberately no "nation" key at all.
                },
                TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
                TemplateVersion = type.TemplateVersion,
            },
            cancellationToken
        );

        var response = await client.PatchAsJsonAsync(
            $"/work-items/re-accreditation/{id}/overseas-sites/{RecyclingOperationsSiteId}/recycling-operations",
            new { operationCodes = s_operationCodesR3 },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await factory
            .RecyclingOperationsAdapter.DidNotReceiveWithAnyArgs()
            .UpdateRecyclingOperationsAsync(default!, cancellationToken);
    }

    [Fact]
    public async Task RecyclingOperations_returns_unauthorized_without_a_forwarded_user_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(
            _fixture,
            userId: null,
            role: "standard",
            nation: "England"
        );
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildRecyclingOperationsCandidate(id, TenantClientId, "England"),
            cancellationToken
        );

        var response = await client.PatchAsJsonAsync(
            $"/work-items/re-accreditation/{id}/overseas-sites/{RecyclingOperationsSiteId}/recycling-operations",
            new { operationCodes = s_operationCodesR3 },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await factory
            .RecyclingOperationsAdapter.DidNotReceiveWithAnyArgs()
            .UpdateRecyclingOperationsAsync(default!, cancellationToken);
    }

    [Fact]
    public async Task RecyclingOperations_returns_not_found_for_missing_work_item()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(
            _fixture,
            role: "standard",
            nation: "England"
        );
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync(
            $"/work-items/re-accreditation/{Guid.NewGuid()}/overseas-sites/{RecyclingOperationsSiteId}/recycling-operations",
            new { operationCodes = s_operationCodesR3 },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RecyclingOperations_passes_through_backend_not_found_as_404()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var adapter = Substitute.For<IOverseasSiteRecyclingOperationsAdapter>();
        adapter
            .UpdateRecyclingOperationsAsync(
                Arg.Any<OverseasSiteRecyclingOperationsRequest>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(OverseasSiteRecyclingOperationsResult.NotFound());
        await using var factory = new ReAccreditationFactory(
            _fixture,
            role: "standard",
            nation: "England",
            recyclingOperationsAdapter: adapter
        );
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildRecyclingOperationsCandidate(id, TenantClientId, "England"),
            cancellationToken
        );

        var response = await client.PatchAsJsonAsync(
            $"/work-items/re-accreditation/{id}/overseas-sites/{RecyclingOperationsSiteId}/recycling-operations",
            new { operationCodes = s_operationCodesR3 },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RecyclingOperations_passes_through_backend_conflict_as_409()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var adapter = Substitute.For<IOverseasSiteRecyclingOperationsAdapter>();
        adapter
            .UpdateRecyclingOperationsAsync(
                Arg.Any<OverseasSiteRecyclingOperationsRequest>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(OverseasSiteRecyclingOperationsResult.Conflict("already updated"));
        await using var factory = new ReAccreditationFactory(
            _fixture,
            role: "standard",
            nation: "England",
            recyclingOperationsAdapter: adapter
        );
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildRecyclingOperationsCandidate(id, TenantClientId, "England"),
            cancellationToken
        );

        var response = await client.PatchAsJsonAsync(
            $"/work-items/re-accreditation/{id}/overseas-sites/{RecyclingOperationsSiteId}/recycling-operations",
            new { operationCodes = s_operationCodesR3 },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task RecyclingOperations_maps_backend_validation_failure_to_400_with_errorCode_and_field()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var adapter = Substitute.For<IOverseasSiteRecyclingOperationsAdapter>();
        adapter
            .UpdateRecyclingOperationsAsync(
                Arg.Any<OverseasSiteRecyclingOperationsRequest>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                OverseasSiteRecyclingOperationsResult.ValidationFailed(
                    "accompanying-code-required",
                    "operationCodes",
                    "R12/R13 require at least one of R3/R4/R5."
                )
            );
        await using var factory = new ReAccreditationFactory(
            _fixture,
            role: "standard",
            nation: "England",
            recyclingOperationsAdapter: adapter
        );
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildRecyclingOperationsCandidate(id, TenantClientId, "England"),
            cancellationToken
        );

        var response = await client.PatchAsJsonAsync(
            $"/work-items/re-accreditation/{id}/overseas-sites/{RecyclingOperationsSiteId}/recycling-operations",
            new { operationCodes = s_operationCodesR12 },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("accompanying-code-required", problem.GetProperty("errorCode").GetString());
        Assert.Equal("operationCodes", problem.GetProperty("field").GetString());
    }

    [Fact]
    public async Task RecyclingOperations_maps_backend_transient_failure_to_500_with_no_errorCode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var adapter = Substitute.For<IOverseasSiteRecyclingOperationsAdapter>();
        adapter
            .UpdateRecyclingOperationsAsync(
                Arg.Any<OverseasSiteRecyclingOperationsRequest>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(OverseasSiteRecyclingOperationsResult.TransientFailure("backend unreachable"));
        await using var factory = new ReAccreditationFactory(
            _fixture,
            role: "standard",
            nation: "England",
            recyclingOperationsAdapter: adapter
        );
        using var client = factory.CreateClient();

        var id = Guid.NewGuid();
        await factory.SeedAsync(
            BuildRecyclingOperationsCandidate(id, TenantClientId, "England"),
            cancellationToken
        );

        var response = await client.PatchAsJsonAsync(
            $"/work-items/re-accreditation/{id}/overseas-sites/{RecyclingOperationsSiteId}/recycling-operations",
            new { operationCodes = s_operationCodesR3 },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.False(problem.TryGetProperty("errorCode", out _));
    }

    [Fact]
    public async Task RecyclingOperations_returns_bad_request_with_wrong_work_item_type()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ReAccreditationFactory(
            _fixture,
            role: "standard",
            nation: "England"
        );
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

        var response = await client.PatchAsJsonAsync(
            $"/work-items/re-accreditation/{id}/overseas-sites/{RecyclingOperationsSiteId}/recycling-operations",
            new { operationCodes = s_operationCodesR3 },
            cancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await factory
            .RecyclingOperationsAdapter.DidNotReceiveWithAnyArgs()
            .UpdateRecyclingOperationsAsync(default!, cancellationToken);
    }

    private static WorkItem BuildRecyclingOperationsCandidate(
        Guid id,
        string submittedBy,
        string nation
    )
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
                ["operatorOrganisationId"] = "500027",
                ["operatorApplicationId"] = "APP-500027",
                ["nation"] = nation,
            },
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion,
        };
    }

    /// <summary>
    /// Same base candidate as <see cref="BuildRecyclingOperationsCandidate"/>,
    /// plus a <c>payload.overseasSites.sites</c> array carrying
    /// <see cref="RecyclingOperationsSiteId"/> and one sibling site — the
    /// shape <see cref="RefreshStoredRecyclingOperationsAsync"/>'s
    /// AC13 write-back needs to find and update a site by id.
    /// </summary>
    private static WorkItem BuildRecyclingOperationsCandidateWithOverseasSites(
        Guid id,
        string submittedBy,
        string nation
    )
    {
        var candidate = BuildRecyclingOperationsCandidate(id, submittedBy, nation);
        candidate.Payload["overseasSites"] = new BsonDocument
        {
            ["sites"] = new BsonArray
            {
                new BsonDocument
                {
                    ["siteId"] = RecyclingOperationsSiteId,
                    ["siteName"] = "Test Overseas Site",
                    ["operationCodes"] = new BsonArray { "R5" },
                },
                new BsonDocument
                {
                    ["siteId"] = "site-other",
                    ["siteName"] = "Untouched Sibling Site",
                    ["operationCodes"] = new BsonArray { "R5" },
                },
            },
        };
        return candidate;
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
            string typeId,
            string operatorApplicationId,
            CancellationToken cancellationToken = default
        ) =>
            inner.FindByOperatorApplicationIdAsync(
                typeId,
                operatorApplicationId,
                cancellationToken
            );

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

    /// <summary>
    /// Throws from SetPayloadFieldAsync for one work item id only — exercises
    /// RefreshStoredRecyclingOperationsAsync's best-effort catch block (the
    /// edit already succeeded on the operator backend by the time this runs,
    /// so a local-snapshot-refresh failure must be swallowed and logged, not
    /// surfaced as an error response).
    /// </summary>
    private sealed class ThrowingSetPayloadFieldPersistence(
        IWorkItemPersistence inner,
        Guid throwId
    ) : IWorkItemPersistence
    {
        public Task<bool> SetPayloadFieldAsync(
            Guid workItemId,
            string fieldName,
            BsonValue value,
            CancellationToken cancellationToken = default
        ) =>
            workItemId == throwId
                ? throw new InvalidOperationException("Simulated snapshot-refresh failure.")
                : inner.SetPayloadFieldAsync(workItemId, fieldName, value, cancellationToken);

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
            string typeId,
            string operatorApplicationId,
            CancellationToken cancellationToken = default
        ) =>
            inner.FindByOperatorApplicationIdAsync(
                typeId,
                operatorApplicationId,
                cancellationToken
            );

        public Task<WorkItemPage> QueryAsync(
            WorkItemQuery query,
            CancellationToken cancellationToken = default
        ) => inner.QueryAsync(query, cancellationToken);

        public Task ReplaceAsync(
            WorkItem workItem,
            CancellationToken cancellationToken = default
        ) => inner.ReplaceAsync(workItem, cancellationToken);
    }

    private sealed class ReAccreditationFactory : WebApplicationFactory<Program>
    {
        private readonly MongoIntegrationFixture _fixture;
        private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("reaccred");
        private readonly string _clientId;
        private readonly string? _userId;
        private readonly string _userName;
        private readonly string? _role;
        private readonly string? _nation;
        private readonly Guid? _raceWorkItemId;
        private readonly Guid? _throwOnSetPayloadFieldWorkItemId;
        private readonly IOperatorBackendPushAdapter? _pushAdapter;
        private readonly IAccreditationNumberAdapter _numberAdapter;
        private readonly IOverseasSiteRecyclingOperationsAdapter _recyclingOperationsAdapter;
        private readonly INotifyClient? _notifyClient;
        private readonly IBackgroundTaskQueue? _backgroundTaskQueue;

        public IReAccreditationDecisionService DecisionService { get; } =
            Substitute.For<IReAccreditationDecisionService>();

        public IReExAccreditationClient ReExClient { get; } =
            Substitute.For<IReExAccreditationClient>();

        public ReAccreditationFactory(
            MongoIntegrationFixture fixture,
            string clientId = TenantClientId,
            string? userId = DefaultUserId,
            string userName = DefaultUserName,
            string? role = null,
            string? nation = null,
            Guid? raceWorkItemId = null,
            Guid? throwOnSetPayloadFieldWorkItemId = null,
            IOperatorBackendPushAdapter? pushAdapter = null,
            IAccreditationNumberAdapter? numberAdapter = null,
            IOverseasSiteRecyclingOperationsAdapter? recyclingOperationsAdapter = null,
            INotifyClient? notifyClient = null,
            IBackgroundTaskQueue? backgroundTaskQueue = null
        )
        {
            _fixture = fixture;
            _clientId = clientId;
            _userId = userId;
            _userName = userName;
            _role = role;
            _nation = nation;
            _raceWorkItemId = raceWorkItemId;
            _throwOnSetPayloadFieldWorkItemId = throwOnSetPayloadFieldWorkItemId;
            _pushAdapter = pushAdapter;
            // RA-448 phase 2: the real HttpAccreditationNumberAdapter (registered
            // unconditionally by ReAccreditationModule) needs a live backend, which
            // this integration suite has none of. Default to a fake returning a
            // fixed, well-formed number so every existing approve/decision test
            // keeps exercising the rest of the pipeline unchanged; pass an explicit
            // numberAdapter to assert against a specific call or failure instead.
            _numberAdapter = numberAdapter ?? BuildDefaultNumberAdapter();
            // RA-469: same reasoning as _numberAdapter — the real
            // HttpOverseasSiteRecyclingOperationsAdapter (registered
            // unconditionally by ReAccreditationModule) needs a live backend
            // this integration suite has none of.
            _recyclingOperationsAdapter =
                recyclingOperationsAdapter ?? BuildDefaultRecyclingOperationsAdapter();
            _notifyClient = notifyClient;
            _backgroundTaskQueue = backgroundTaskQueue;
        }

        private static IAccreditationNumberAdapter BuildDefaultNumberAdapter()
        {
            var adapter = Substitute.For<IAccreditationNumberAdapter>();
            adapter
                .GenerateOrUpdateAccreditationNumberAsync(
                    Arg.Any<AccreditationNumberRequest>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(AccreditationNumberResult.Success("A25ER5000270036WO"));
            return adapter;
        }

        private static IOverseasSiteRecyclingOperationsAdapter BuildDefaultRecyclingOperationsAdapter()
        {
            var adapter = Substitute.For<IOverseasSiteRecyclingOperationsAdapter>();
            adapter
                .UpdateRecyclingOperationsAsync(
                    Arg.Any<OverseasSiteRecyclingOperationsRequest>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(OverseasSiteRecyclingOperationsResult.Success("{\"id\":\"site-1\"}"));
            return adapter;
        }

        public IWorkItemPersistence Persistence =>
            Services.GetRequiredService<IWorkItemPersistence>();

        public IOverseasSiteRecyclingOperationsAdapter RecyclingOperationsAdapter =>
            _recyclingOperationsAdapter;

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
        /// Resolving <see cref="IWorkItemPersistence"/> is what actually does
        /// this: its <c>DefineIndexes</c> owns every production index on this
        /// collection, including the unique + sparse index on
        /// <c>payload.accreditationId</c> (see its own doc comment for why —
        /// that index used to also be read by the now-retired
        /// <c>AccreditationIdLookup</c>, whose own <c>DefineIndexes</c> was
        /// deliberately empty for exactly this reason). That is exactly why
        /// the duplicate-key bug in the current-query stamp reached the real
        /// stack with 968 tests green: nothing had forced this construction
        /// yet at the time.
        /// </summary>
        private void EnsureProductionIndexes()
        {
            _ = Persistence;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            if (_notifyClient is not null)
            {
                // The notification hook is only registered when Notify is on.
                builder.UseSetting("Notify:Enabled", "true");
            }

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
                    if (_throwOnSetPayloadFieldWorkItemId is { } throwId)
                    {
                        return new ThrowingSetPayloadFieldPersistence(real, throwId);
                    }
                    return real;
                });

                services.AddSingleton(DecisionService);
                services.AddSingleton(ReExClient);

                if (_pushAdapter is not null)
                {
                    services.RemoveAll<IOperatorBackendPushAdapter>();
                    services.AddSingleton(_pushAdapter);
                }

                // RA-448 phase 2: always substitute — the real
                // HttpAccreditationNumberAdapter needs a live backend this
                // integration suite doesn't have.
                services.RemoveAll<IAccreditationNumberAdapter>();
                services.AddSingleton(_numberAdapter);

                // RA-469: same reasoning — the real
                // HttpOverseasSiteRecyclingOperationsAdapter needs a live
                // backend this integration suite doesn't have.
                services.RemoveAll<IOverseasSiteRecyclingOperationsAdapter>();
                services.AddSingleton(_recyclingOperationsAdapter);

                // RA-527: optional, so a test can see whether an email was
                // sent or a background job (publishing, a status push) queued.
                if (_notifyClient is not null)
                {
                    services.RemoveAll<INotifyClient>();
                    services.AddSingleton(_notifyClient);
                }
                if (_backgroundTaskQueue is not null)
                {
                    services.RemoveAll<IBackgroundTaskQueue>();
                    services.AddSingleton(_backgroundTaskQueue);
                }
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
            if (_role is not null)
            {
                client.DefaultRequestHeaders.Add("x-cdp-user-role", _role);
            }
            if (_nation is not null)
            {
                client.DefaultRequestHeaders.Add("x-cdp-user-nation", _nation);
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
