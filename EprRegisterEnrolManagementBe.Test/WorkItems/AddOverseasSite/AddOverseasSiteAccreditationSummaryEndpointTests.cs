using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using NSubstitute;
using static EprRegisterEnrolManagementBe.Test.TestSupport.AddOverseasSiteAppFactory;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.AddOverseasSite;

/// <summary>
/// RA-769: <c>GET /work-items/{id}/accreditation-summary</c>, the live accreditation behind an In-Year Change
/// case's Accreditation summary tab. Driven through the real ASP.NET pipeline with the production modules
/// registered, against ephemeral MongoDB, from a submission posted as the operator backend posts it. The operator
/// backend read adapter is a fake.
/// </summary>
public sealed class AddOverseasSiteAccreditationSummaryEndpointTests(MongoIntegrationFixture fixture)
{
    private const string ApplicationId = "6a1b2c3d4e5f6a1b2c3d4e5f";

    private static readonly OperatorAccreditationSummary Live = new(
        OrganisationName: "Acme Recycling Ltd",
        OrganisationNumber: 500500,
        AccreditationNumber: "A26ER5003240164GR",
        RegistrationNumber: "R26ER5003240164GR",
        AccreditationYear: 2026,
        WasteProcessingType: "exporter",
        Material: "glass",
        GlassRecyclingProcess: "glass_re_melt",
        SiteAddress: "1 High Street, Leeds, LS1 1AA",
        CompanyRegisteredAddress: "2 Registered Road, York, YO1 1AA",
        Prns: new OperatorSummaryPrns(
            "UpTo500",
            [new OperatorSummaryAuthoriser("Ada Authoriser", "ada@example.com")]
        ),
        BusinessPlan: new OperatorSummaryBusinessPlan { OtherPercent = 100, OtherDetail = "Everything else" },
        OverseasSites:
        [
            new OperatorSummarySite(
                SiteId: 7,
                OrsId: "001",
                SiteName: "Speira GmbH",
                SiteAddress: null,
                AddressLine1: "1 Test Strasse",
                AddressLine2: null,
                TownOrCity: "Bremen",
                Country: "Germany",
                Coordinates: "51.847,7.123",
                ContactName: "Hans Contact",
                ContactEmail: "hans@example.de",
                ContactPhone: "+49 123",
                OperationCodes: ["R4"],
                Code1: "B1010",
                Code2: null,
                Code3: null,
                RepatriatedLoads: "As per article 18",
                ConditionsOfExport: true,
                IsEu: true,
                IsOecd: true
            ),
        ]
    );

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AddOverseasSiteAppFactory NewFactory(
        MongoIntegrationFixture fixture,
        IAccreditationSummaryAdapter adapter,
        bool testOrganisations = false
    )
    {
        return new AddOverseasSiteAppFactory(fixture)
        {
            Settings = new Dictionary<string, string>
            {
                [TestOrganisations.EnabledKey] = testOrganisations ? "true" : "false",
            },
            ConfigureTestServices = services =>
            {
                services.RemoveAll<IAccreditationSummaryAdapter>();
                services.AddSingleton(adapter);
            },
        };
    }

    private static IAccreditationSummaryAdapter AdapterReturning(AccreditationSummaryReadResult result)
    {
        var adapter = Substitute.For<IAccreditationSummaryAdapter>();
        adapter
            .GetAccreditationSummaryAsync(Arg.Any<AccreditationSummaryReadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));
        return adapter;
    }

    // An add-ORS submission as the operator backend posts it, with the fields the summary resolves from.
    private static async Task<Guid> SubmitAsync(
        AddOverseasSiteAppFactory factory,
        HttpClient client,
        string operatorRequestId,
        string organisationId = "org-1",
        string? applicationId = ApplicationId
    )
    {
        var body = AddOverseasSiteSubmissions.Body(operatorRequestId, siteCount: 1);
        var payload = body["payload"]!.AsObject();
        payload["operatorOrganisationId"] = organisationId;
        if (applicationId is not null)
        {
            payload["accreditationApplicationId"] = applicationId;
        }
        var response = await client.PostAsJsonAsync("/work-items", body, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return created.GetProperty("workItems")[0].GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_case_with_an_accreditation_shows_the_live_read_and_its_start_date()
    {
        var adapter = AdapterReturning(AccreditationSummaryReadResult.Success(Live));
        await using var factory = NewFactory(fixture, adapter);
        using var client = factory.CreateClient();
        var id = await SubmitAsync(factory, client, "summary-live", organisationId: "org-live");

        var response = await client.GetAsync($"/work-items/{id}/accreditation-summary", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var accreditation = body.GetProperty("accreditation");
        Assert.Equal("Acme Recycling Ltd", accreditation.GetProperty("organisationName").GetString());
        Assert.Equal(500500, accreditation.GetProperty("organisationNumber").GetInt32());
        Assert.Equal("A26ER5003240164GR", accreditation.GetProperty("accreditationNumber").GetString());
        Assert.Equal("R26ER5003240164GR", accreditation.GetProperty("registrationNumber").GetString());
        Assert.Equal(2026, accreditation.GetProperty("accreditationYear").GetInt32());
        Assert.Equal("exporter", accreditation.GetProperty("wasteProcessingType").GetString());
        Assert.Equal("glass", accreditation.GetProperty("material").GetString());
        Assert.Equal("glass_re_melt", accreditation.GetProperty("glassRecyclingProcess").GetString());
        Assert.Equal("1 High Street, Leeds, LS1 1AA", accreditation.GetProperty("siteAddress").GetString());
        Assert.Equal(
            "2 Registered Road, York, YO1 1AA",
            accreditation.GetProperty("companyRegisteredAddress").GetString()
        );
        // Only the summary's own fields go on the wire, nothing derived.
        Assert.Equal(
            [
                "organisationName",
                "organisationNumber",
                "accreditationNumber",
                "registrationNumber",
                "accreditationYear",
                "wasteProcessingType",
                "material",
                "glassRecyclingProcess",
                "siteAddress",
                "companyRegisteredAddress",
                "prns",
                "businessPlan",
                "overseasSites",
            ],
            accreditation.EnumerateObject().Select(p => p.Name)
        );
        var prns = accreditation.GetProperty("prns");
        Assert.Equal("UpTo500", prns.GetProperty("plannedTonnageBand").GetString());
        var authoriser = Assert.Single(prns.GetProperty("authorisers").EnumerateArray());
        Assert.Equal("Ada Authoriser", authoriser.GetProperty("fullName").GetString());
        Assert.Equal("ada@example.com", authoriser.GetProperty("email").GetString());
        var plan = accreditation.GetProperty("businessPlan");
        Assert.Equal(100, plan.GetProperty("otherPercent").GetInt32());
        Assert.Equal("Everything else", plan.GetProperty("otherDetail").GetString());
        var site = Assert.Single(accreditation.GetProperty("overseasSites").EnumerateArray());
        Assert.Equal("001", site.GetProperty("orsId").GetString());
        Assert.Equal("Speira GmbH", site.GetProperty("siteName").GetString());
        Assert.Equal("Bremen", site.GetProperty("townOrCity").GetString());
        Assert.Equal("R4", Assert.Single(site.GetProperty("operationCodes").EnumerateArray()).GetString());
        Assert.True(site.GetProperty("isEu").GetBoolean());
        // No re-accreditation work item holds this accreditation number: 1 January of the accreditation year.
        Assert.Equal("2026-01-01", body.GetProperty("accreditationStartDate").GetString());
        await adapter
            .Received(1)
            .GetAccreditationSummaryAsync(
                Arg.Is<AccreditationSummaryReadRequest>(r =>
                    r.OrganisationId == "org-live" && r.ApplicationId == ApplicationId
                ),
                Arg.Any<CancellationToken>()
            );
    }

    // The re-accreditation work item that issued an accreditation: its number and start date on the payload.
    private static async Task StoreReAccreditationAsync(
        AddOverseasSiteAppFactory factory,
        string stateId,
        string accreditationNumber,
        string? startDate
    )
    {
        var type = new ReAccreditationType();
        var payload = new BsonDocument { ["accreditationId"] = accreditationNumber };
        if (startDate is not null)
        {
            payload["accreditationStartDate"] = startDate;
        }
        await factory.Persistence.CreateAsync(
            new WorkItem
            {
                Id = Guid.NewGuid(),
                TypeId = ReAccreditationType.Id,
                StateId = stateId,
                SubmittedBy = "test-client",
                TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
                TemplateVersion = type.TemplateVersion,
                Payload = payload,
            },
            Ct
        );
    }

    private async Task<(HttpStatusCode Status, string? StartDate)> GetStartDateAsync(
        string stateId,
        string? storedStartDate,
        string storedNumber = "A26ER5003240164GR"
    )
    {
        var adapter = AdapterReturning(AccreditationSummaryReadResult.Success(Live));
        await using var factory = NewFactory(fixture, adapter);
        using var client = factory.CreateClient();
        await StoreReAccreditationAsync(factory, stateId, storedNumber, storedStartDate);
        var id = await SubmitAsync(factory, client, "summary-start-date");

        var response = await client.GetAsync($"/work-items/{id}/accreditation-summary", Ct);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return (response.StatusCode, null);
        }
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return (response.StatusCode, body.GetProperty("accreditationStartDate").GetString());
    }

    [Fact]
    public async Task An_accreditation_granted_mid_year_shows_its_stored_start_date()
    {
        var (status, startDate) = await GetStartDateAsync("approved", "2026-06-15");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("2026-06-15", startDate);
    }

    [Fact]
    public async Task A_work_item_pending_contact_does_not_supply_the_start_date_even_if_it_carries_one()
    {
        var (status, startDate) = await GetStartDateAsync("approved-pending-contact", "2026-06-15");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("2026-01-01", startDate);
    }

    [Fact]
    public async Task A_granted_work_item_without_a_stored_date_falls_back_to_1_january()
    {
        var (status, startDate) = await GetStartDateAsync("approved", storedStartDate: null);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("2026-01-01", startDate);
    }

    [Fact]
    public async Task Only_the_work_item_with_this_accreditation_number_supplies_the_start_date()
    {
        var (status, startDate) = await GetStartDateAsync("approved", "2026-06-15", storedNumber: "A26ER5003240199GR");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("2026-01-01", startDate);
    }

    [Fact]
    public async Task A_failed_work_item_lookup_is_a_service_unavailable_never_a_guessed_date()
    {
        var adapter = AdapterReturning(AccreditationSummaryReadResult.Success(Live));
        await using var factory = new AddOverseasSiteAppFactory(fixture)
        {
            ConfigureTestServices = services =>
            {
                services.RemoveAll<IAccreditationSummaryAdapter>();
                services.AddSingleton(adapter);
                var real = services.Last(d => d.ServiceType == typeof(IWorkItemPersistence));
                services.Remove(real);
                services.AddSingleton<IWorkItemPersistence>(sp => new FailingLookupPersistence(
                    (IWorkItemPersistence)(
                        real.ImplementationInstance
                        ?? real.ImplementationFactory?.Invoke(sp)
                        ?? ActivatorUtilities.CreateInstance(sp, real.ImplementationType!)
                    )
                ));
            },
        };
        using var client = factory.CreateClient();
        var id = await SubmitAsync(factory, client, "summary-lookup-failed");

        var response = await client.GetAsync($"/work-items/{id}/accreditation-summary", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private sealed class FailingLookupPersistence(IWorkItemPersistence inner) : ForwardingWorkItemPersistence(inner)
    {
        public override Task<WorkItem?> FindBySubmissionKeyAsync(
            string typeId,
            string payloadField,
            string value,
            CancellationToken cancellationToken = default
        ) => Task.FromException<WorkItem?>(new InvalidOperationException("Injected lookup failure"));
    }

    [Fact]
    public async Task An_unknown_work_item_is_not_found()
    {
        var adapter = AdapterReturning(AccreditationSummaryReadResult.Success(Live));
        await using var factory = NewFactory(fixture, adapter);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/work-items/{Guid.NewGuid()}/accreditation-summary", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await adapter.DidNotReceiveWithAnyArgs().GetAccreditationSummaryAsync(default!, default);
    }

    [Fact]
    public async Task A_work_item_type_without_the_tab_is_rejected_as_the_wrong_type()
    {
        var adapter = AdapterReturning(AccreditationSummaryReadResult.Success(Live));
        await using var factory = NewFactory(fixture, adapter);
        using var client = factory.CreateClient();
        var type = new ReAccreditationType();
        var id = Guid.NewGuid();
        await factory.Persistence.CreateAsync(
            new WorkItem
            {
                Id = id,
                TypeId = ReAccreditationType.Id,
                StateId = "submitted",
                SubmittedBy = "test-client",
                TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
                TemplateVersion = type.TemplateVersion,
            },
            Ct
        );

        var response = await client.GetAsync($"/work-items/{id}/accreditation-summary", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("wrong-work-item-type", problem.GetProperty("errorCode").GetString());
        await adapter.DidNotReceiveWithAnyArgs().GetAccreditationSummaryAsync(default!, default);
    }

    [Fact]
    public async Task A_registration_only_case_has_no_summary_and_makes_no_operator_call()
    {
        var adapter = AdapterReturning(AccreditationSummaryReadResult.Success(Live));
        await using var factory = NewFactory(fixture, adapter);
        using var client = factory.CreateClient();
        var id = await SubmitAsync(factory, client, "summary-registration-only", applicationId: null);

        var response = await client.GetAsync($"/work-items/{id}/accreditation-summary", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await adapter.DidNotReceiveWithAnyArgs().GetAccreditationSummaryAsync(default!, default);
    }

    [Fact]
    public async Task An_accreditation_the_operator_backend_does_not_hold_as_approved_is_not_found()
    {
        var adapter = AdapterReturning(AccreditationSummaryReadResult.NotFound());
        await using var factory = NewFactory(fixture, adapter);
        using var client = factory.CreateClient();
        var id = await SubmitAsync(factory, client, "summary-not-found");

        var response = await client.GetAsync($"/work-items/{id}/accreditation-summary", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_failed_read_is_a_service_unavailable_never_a_guess()
    {
        var adapter = AdapterReturning(AccreditationSummaryReadResult.TransientFailure("operator backend is down"));
        await using var factory = NewFactory(fixture, adapter);
        using var client = factory.CreateClient();
        var id = await SubmitAsync(factory, client, "summary-transient");

        var response = await client.GetAsync($"/work-items/{id}/accreditation-summary", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task A_disabled_operator_backend_is_a_service_unavailable()
    {
        // The real adapter, with OperatorBackendApi:Enabled false (the default): a transient failure.
        await using var factory = new AddOverseasSiteAppFactory(fixture);
        using var client = factory.CreateClient();
        var id = await SubmitAsync(factory, client, "summary-disabled");

        var response = await client.GetAsync($"/work-items/{id}/accreditation-summary", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task A_test_organisation_is_answered_from_the_stub_even_without_an_application_id()
    {
        var adapter = AdapterReturning(AccreditationSummaryReadResult.NotFound());
        await using var factory = NewFactory(fixture, adapter, testOrganisations: true);
        using var client = factory.CreateClient();
        var id = await SubmitAsync(
            factory,
            client,
            "summary-stub",
            organisationId: "e2e-summary-stub",
            applicationId: null
        );

        var response = await client.GetAsync($"/work-items/{id}/accreditation-summary", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var accreditation = body.GetProperty("accreditation");
        Assert.Equal("E2E Test Organisation Ltd", accreditation.GetProperty("organisationName").GetString());
        Assert.Equal(500999, accreditation.GetProperty("organisationNumber").GetInt32());
        Assert.Equal("A26ER9999999999PL", accreditation.GetProperty("accreditationNumber").GetString());
        Assert.Equal("R26ER9999999999PL", accreditation.GetProperty("registrationNumber").GetString());
        Assert.Equal(2026, accreditation.GetProperty("accreditationYear").GetInt32());
        Assert.Equal("exporter", accreditation.GetProperty("wasteProcessingType").GetString());
        Assert.Equal("plastic", accreditation.GetProperty("material").GetString());
        var stubPrns = accreditation.GetProperty("prns");
        Assert.Equal("UpTo5000", stubPrns.GetProperty("plannedTonnageBand").GetString());
        Assert.NotEmpty(stubPrns.GetProperty("authorisers").EnumerateArray());
        Assert.NotEqual(
            JsonValueKind.Null,
            accreditation.GetProperty("businessPlan").GetProperty("newInfrastructurePercent").ValueKind
        );
        // Several sites, in ORS id order, none of them marked new.
        var stubSites = accreditation.GetProperty("overseasSites").EnumerateArray().ToList();
        Assert.True(stubSites.Count >= 3);
        Assert.Equal(
            stubSites.Select(s => s.GetProperty("orsId").GetString()).OrderBy(id => id, StringComparer.Ordinal),
            stubSites.Select(s => s.GetProperty("orsId").GetString())
        );
        Assert.All(stubSites, s => Assert.False(string.IsNullOrWhiteSpace(s.GetProperty("siteName").GetString())));
        Assert.DoesNotContain("isNewSite", accreditation.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("2026-01-01", body.GetProperty("accreditationStartDate").GetString());
        await adapter.DidNotReceiveWithAnyArgs().GetAccreditationSummaryAsync(default!, default);
    }

    [Fact]
    public async Task The_stub_is_inactive_when_test_organisations_are_off()
    {
        var adapter = AdapterReturning(AccreditationSummaryReadResult.NotFound());
        await using var factory = NewFactory(fixture, adapter, testOrganisations: false);
        using var client = factory.CreateClient();
        var noApplication = await SubmitAsync(
            factory,
            client,
            "summary-stub-off-1",
            organisationId: "e2e-summary-stub-off",
            applicationId: null
        );
        var withApplication = await SubmitAsync(
            factory,
            client,
            "summary-stub-off-2",
            organisationId: "e2e-summary-stub-off"
        );

        var first = await client.GetAsync($"/work-items/{noApplication}/accreditation-summary", Ct);
        var second = await client.GetAsync($"/work-items/{withApplication}/accreditation-summary", Ct);

        // No stub: the first has no application id, so no accreditation; the second is read live (here: not found).
        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        await adapter
            .Received(1)
            .GetAccreditationSummaryAsync(
                Arg.Is<AccreditationSummaryReadRequest>(r => r.OrganisationId == "e2e-summary-stub-off"),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Only_the_lower_case_e2e_prefix_makes_a_test_organisation()
    {
        var adapter = AdapterReturning(AccreditationSummaryReadResult.NotFound());
        await using var factory = NewFactory(fixture, adapter, testOrganisations: true);
        using var client = factory.CreateClient();
        var id = await SubmitAsync(
            factory,
            client,
            "summary-upper-prefix",
            organisationId: "E2E-summary",
            applicationId: null
        );

        var response = await client.GetAsync($"/work-items/{id}/accreditation-summary", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
