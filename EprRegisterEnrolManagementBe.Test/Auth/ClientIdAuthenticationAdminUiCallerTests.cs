using System.Net;
using System.Text;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.Auth;

/// <summary>
/// RA-751 / ADR-0010: the Admin UI (<c>epr-register-enrol-admin</c>) is a
/// read-only caller. It authenticates with its own secret,
/// <c>AUTH_SHARED_SECRET__ADMIN_UI</c>, and may only GET (or HEAD) the
/// endpoints that opt in to <see cref="CallerAccessPolicies.ReadOnlyCallerAllowed"/>:
/// <c>GET /work-items</c> and <c>GET /work-items/re-accreditation/states</c>
/// (RA-751), and <c>GET /work-items/{id}</c> and
/// <c>GET /work-items/re-accreditation/{id}/prior-year</c> (RA-752, the Admin
/// UI's Work item page). Every other request it makes is refused with 403 and
/// logged at Error.
/// Production host throughout unless stated, so the handler fails closed.
/// </summary>
public class ClientIdAuthenticationAdminUiCallerTests
{
    private const string AdminSecret = "admin-ui-secret";
    private const string ManagementFeSecret = "management-fe-secret";
    private const string SupportRole = "Waste.SupportUser.ReadOnly";
    private static readonly Guid s_workItemId = Guid.Parse("00000751-0000-4000-8000-000000000001");

    private static (BareFactory Factory, WebApplicationFactory<Program> App, CapturingLoggerProvider Logs) NewApp(
        string environment = "Production",
        string? adminSecret = AdminSecret,
        IReExAccreditationClient? reExClient = null
    )
    {
        var overrides = new Dictionary<string, string?>
        {
            ["AUTH_SHARED_SECRET:MANAGEMENT_FE"] = ManagementFeSecret,
        };
        if (adminSecret is not null)
        {
            overrides["AUTH_SHARED_SECRET:ADMIN_UI"] = adminSecret;
        }
        var factory = new BareFactory(environment: environment, configOverrides: overrides);
        factory
            .MockPersistence.QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(Array.Empty<WorkItem>(), 0, 1, 50));
        var logs = new CapturingLoggerProvider();
        var app = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                if (reExClient is not null)
                {
                    services.RemoveAll<IReExAccreditationClient>();
                    services.AddSingleton(reExClient);
                }
                services.RemoveAll<ILoggerFactory>();
                services.AddSingleton<ILoggerFactory>(_ =>
                    LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs))
                );
            })
        );
        return (factory, app, logs);
    }

    private static HttpClient SignedClient(
        BareFactory factory,
        WebApplicationFactory<Program> app,
        string clientId,
        string secret,
        string nonce,
        string userName = "Support User"
    )
    {
        var timestamp = factory.FakeTime.GetUtcNow().ToString("O");
        var signature = ClientIdAuthenticationHandler.ComputeSignature(
            secret,
            new ClientIdSignaturePayload(clientId, "entra-oid-123", userName, timestamp, nonce, Role: SupportRole)
        );
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, clientId);
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultUserIdHeaderName, "entra-oid-123");
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultUserNameHeaderName, userName);
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultRoleHeaderName, SupportRole);
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultTimestampHeaderName, timestamp);
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultNonceHeaderName, nonce);
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultSignatureHeaderName, signature);
        return client;
    }

    private static HttpClient AdminClient(BareFactory factory, WebApplicationFactory<Program> app, string nonce) =>
        SignedClient(factory, app, CallerAccessPolicies.AdminUiClientIdDefault, AdminSecret, nonce);

    [Fact]
    public void Admin_ui_client_id_is_epr_register_enrol_admin()
    {
        // The id the Admin UI asserts; agreed with that service (RA-751).
        Assert.Equal("epr-register-enrol-admin", CallerAccessPolicies.AdminUiClientIdDefault);
    }

    [Fact]
    public void Extended_signature_for_a_support_user_with_no_nation_matches_the_known_value()
    {
        // Pinned so the Admin UI can assert the same bytes on its side:
        // eight lines, nation signed as an empty line.
        var signature = ClientIdAuthenticationHandler.ComputeSignature(
            "admin-test-secret",
            new ClientIdSignaturePayload(
                "epr-register-enrol-admin",
                "entra-oid-123",
                "Support User",
                "2026-11-25T10:00:00.000Z",
                "nonce-abc",
                Role: SupportRole
            )
        );

        Assert.Equal("YS2+6vd9iBCjTS30LVJvJZOtIXqudTx0KKsbuX7/PjI=", signature);
    }

    [Fact]
    public async Task Admin_ui_can_list_work_items_across_every_nation()
    {
        var (factory, app, logs) = NewApp();
        await using var _f = factory;
        await using var _a = app;
        using var client = AdminClient(factory, app, "nonce-admin-list");

        var response = await client.GetAsync("/work-items?pageSize=50", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // RA-751 epic: upstream logs show the calling client and the user.
        var access = Assert.Single(
            logs.Records,
            r => r.Level == LogLevel.Information && r.Message.StartsWith("Read-only caller", StringComparison.Ordinal)
        );
        Assert.Equal(CallerAccessPolicies.AdminUiClientIdDefault, access.Properties["ClientId"]);
        Assert.Equal("entra-oid-123", access.Properties["UserId"]);
        // No nation scoping is applied for a support user: the query reaches
        // persistence with no nation restriction.
        await factory
            .MockPersistence.Received(1)
            .QueryAsync(
                Arg.Is<WorkItemQuery>(q => q.PageSize == 50 && q.Nations == null),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Admin_ui_can_read_the_re_accreditation_states()
    {
        var (factory, app, _) = NewApp();
        await using var _f = factory;
        await using var _a = app;
        using var client = AdminClient(factory, app, "nonce-admin-states");

        var response = await client.GetAsync(
            "/work-items/re-accreditation/states",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public static TheoryData<string, string> RefusedRequests =>
        new()
        {
            { "POST", "/work-items" },
            { "POST", $"/work-items/{s_workItemId}/assign" },
            { "POST", $"/work-items/{s_workItemId}/unassign" },
            { "POST", $"/work-items/{s_workItemId}/notes" },
            { "POST", $"/work-items/{s_workItemId}/actions/duly-make" },
            { "POST", $"/work-items/re-accreditation/{s_workItemId}/decision-rationale" },
            { "PATCH", $"/work-items/re-accreditation/{s_workItemId}/overseas-sites/site-1/recycling-operations" },
            // RA-752: neither management-fe's work item page nor the Admin UI
            // calls the recommendation read, so it stays closed.
            { "GET", $"/work-items/re-accreditation/{s_workItemId}/recommendation" },
            // RA-770's Related change requests read: plain RequireAuthorization(),
            // so the default (not read-only) policy keeps the Admin UI out.
            { "GET", $"/work-items/{s_workItemId}/related-work-items" },
        };

    [Theory]
    [MemberData(nameof(RefusedRequests))]
    public async Task Admin_ui_is_refused_with_403_and_an_error_log_outside_its_read_endpoints(
        string method,
        string path
    )
    {
        var (factory, app, logs) = NewApp();
        await using var _f = factory;
        await using var _a = app;
        using var client = AdminClient(factory, app, $"nonce-admin-{method}-{path.GetHashCode()}");
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PATCH")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var refusal = Assert.Single(
            logs.Records,
            r => r.Level == LogLevel.Error && r.Message.StartsWith("Read-only caller", StringComparison.Ordinal)
        );
        Assert.Equal(CallerAccessPolicies.AdminUiClientIdDefault, refusal.Properties["ClientId"]);
        Assert.Equal("entra-oid-123", refusal.Properties["UserId"]);
        Assert.Equal(method, refusal.Properties["Method"]);
        Assert.Equal(path, refusal.Properties["Path"]);
        // The request never reached a handler: no work item was read or written.
        await factory
            .MockPersistence.DidNotReceive()
            .GetByIdAsync(s_workItemId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The full set of endpoints open to a read-only caller, read from the
    /// routing table, so opening another route (or a write) fails here until
    /// ADR-0010 and docs/work-items.md are updated with it.
    /// </summary>
    [Fact]
    public async Task Only_the_four_read_endpoints_are_open_to_read_only_callers_and_all_are_get_only()
    {
        var (factory, app, _) = NewApp();
        await using var _f = factory;
        await using var _a = app;
        using var scope = app.Services.CreateScope();
        var endpoints = scope
            .ServiceProvider.GetRequiredService<IEnumerable<EndpointDataSource>>()
            .SelectMany(s => s.Endpoints)
            .Where(e =>
                e.Metadata.GetOrderedMetadata<IAuthorizeData>()
                    .Any(a => a.Policy == CallerAccessPolicies.ReadOnlyCallerAllowed)
            )
            .ToList();

        Assert.Equal(
            ["GetReAccreditationPriorYear", "GetReAccreditationStates", "GetWorkItemById", "ListWorkItems"],
            endpoints.Select(e => e.Metadata.GetMetadata<EndpointNameMetadata>()?.EndpointName).Order()
        );
        Assert.All(
            endpoints,
            e => Assert.Equal(["GET"], e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
        );
    }

    private static WorkItem StoredWorkItem() =>
        new()
        {
            Id = s_workItemId,
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            SubmittedAt = DateTime.Parse("2026-04-01T09:00:00Z").ToUniversalTime(),
            LastModifiedAt = DateTime.Parse("2026-04-02T09:00:00Z").ToUniversalTime(),
            SubmittedBy = "operator-backend",
            Payload = new BsonDocument
            {
                ["applicationReference"] = "APP-752",
                ["operatorOrganisationId"] = "org-752",
                ["operatorRegistrationId"] = "reg-752",
                ["previousAccreditationYear"] = 2025,
            },
            Notes =
            {
                new WorkItemNote
                {
                    Id = Guid.Parse("00000752-0000-4000-8000-000000000002"),
                    Text = "Checked the evidence",
                    CreatedAt = DateTime.Parse("2026-04-02T08:00:00Z").ToUniversalTime(),
                    CreatedBy = "caseworker-1",
                    CreatedByName = "Case Worker",
                },
            },
            AuditLog =
            {
                new WorkItemAuditEntry
                {
                    Id = Guid.Parse("00000752-0000-4000-8000-000000000003"),
                    Action = "status-push-sent",
                    ActionDisplayName = "Status sent to the Registration & Accreditation service",
                    CreatedAt = DateTime.Parse("2026-04-02T09:00:00Z").ToUniversalTime(),
                    Details = new Dictionary<string, string?> { ["toStateId"] = "submitted" },
                    StateId = "submitted",
                },
            },
        };

    [Fact]
    public async Task Admin_ui_can_read_a_work_item_with_its_payload_notes_and_audit_log()
    {
        // RA-752 AC08: the Document tab shows the payload, notes and audit log,
        // including the status pushes to the operator journey.
        var (factory, app, logs) = NewApp();
        await using var _f = factory;
        await using var _a = app;
        factory.MockPersistence.GetByIdAsync(s_workItemId, Arg.Any<CancellationToken>()).Returns(StoredWorkItem());
        using var client = AdminClient(factory, app, "nonce-admin-get-by-id");

        var response = await client.GetAsync($"/work-items/{s_workItemId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        var root = body.RootElement;
        Assert.Equal(s_workItemId, root.GetProperty("id").GetGuid());
        Assert.Equal("APP-752", root.GetProperty("payload").GetProperty("applicationReference").GetString());
        Assert.Equal("Checked the evidence", Assert.Single(root.GetProperty("notes").EnumerateArray()).GetProperty("text").GetString());
        var push = Assert.Single(root.GetProperty("auditLog").EnumerateArray());
        Assert.Equal("status-push-sent", push.GetProperty("action").GetString());
        Assert.Equal("submitted", push.GetProperty("details").GetProperty("toStateId").GetString());
        var access = Assert.Single(
            logs.Records,
            r => r.Level == LogLevel.Information && r.Message.StartsWith("Read-only caller", StringComparison.Ordinal)
        );
        Assert.Equal($"/work-items/{s_workItemId}", access.Properties["Path"]);
    }

    [Fact]
    public async Task Admin_ui_reads_the_same_work_item_as_management_fe()
    {
        // Unlike the list (ADR-0010 decision 5), the single-item read is not
        // projected for a read-only caller: RA-752 AC08's Document tab shows
        // the payload, notes and audit log, and the Admin UI masks names and
        // emails itself (AC10). So the Admin UI sees exactly what a
        // caseworker's frontend sees, availableActions included.
        var (factory, app, _) = NewApp();
        await using var _f = factory;
        await using var _a = app;
        factory.MockPersistence.GetByIdAsync(s_workItemId, Arg.Any<CancellationToken>()).Returns(_ => StoredWorkItem());
        using var admin = AdminClient(factory, app, "nonce-admin-same-view");
        using var frontend = SignedClient(factory, app, "frontend", ManagementFeSecret, "nonce-fe-same-view");

        var adminBody = await admin.GetStringAsync($"/work-items/{s_workItemId}", TestContext.Current.CancellationToken);
        var frontendBody = await frontend.GetStringAsync(
            $"/work-items/{s_workItemId}",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(frontendBody, adminBody);
    }

    [Fact]
    public async Task Admin_ui_gets_a_plain_404_not_403_for_a_work_item_that_does_not_exist()
    {
        var (factory, app, logs) = NewApp();
        await using var _f = factory;
        await using var _a = app;
        using var client = AdminClient(factory, app, "nonce-admin-missing");

        var response = await client.GetAsync($"/work-items/{s_workItemId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("errorCode", text, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Records, r => r.Level == LogLevel.Error);
        await factory.MockPersistence.Received(1).GetByIdAsync(s_workItemId, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("/work-items/not-a-guid")]
    [InlineData("/work-items/12345")]
    [InlineData("/work-items/re-accreditation/not-a-guid/prior-year")]
    public async Task Admin_ui_gets_404_for_an_id_that_cannot_be_a_work_item(string path)
    {
        // The {id:guid} route constraint does not match, so no endpoint runs
        // and nothing is authorized or read: 404, never 403.
        var (factory, app, logs) = NewApp();
        await using var _f = factory;
        await using var _a = app;
        using var client = AdminClient(factory, app, $"nonce-admin-malformed-{path.GetHashCode()}");

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // No access or refusal log: authorization never ran.
        Assert.DoesNotContain(logs.Records, r => r.Message.StartsWith("Read-only caller", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Admin_ui_can_read_the_prior_year_data()
    {
        var reEx = Substitute.For<IReExAccreditationClient>();
        reEx.GetPriorYearAsync("org-752", "reg-752", 2025, Arg.Any<CancellationToken>())
            .Returns(new PriorYearAccreditationDto { Year = 2025, TonnageBand = "UpTo5000" });
        var (factory, app, _) = NewApp(reExClient: reEx);
        await using var _f = factory;
        await using var _a = app;
        factory.MockPersistence.GetByIdAsync(s_workItemId, Arg.Any<CancellationToken>()).Returns(StoredWorkItem());
        using var client = AdminClient(factory, app, "nonce-admin-prior-year");

        var response = await client.GetAsync(
            $"/work-items/re-accreditation/{s_workItemId}/prior-year",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        Assert.Equal(2025, body.RootElement.GetProperty("year").GetInt32());
    }

    [Fact]
    public async Task Admin_ui_gets_404_not_403_for_prior_year_data_of_a_missing_work_item()
    {
        var reEx = Substitute.For<IReExAccreditationClient>();
        var (factory, app, logs) = NewApp(reExClient: reEx);
        await using var _f = factory;
        await using var _a = app;
        using var client = AdminClient(factory, app, "nonce-admin-prior-year-missing");

        var response = await client.GetAsync(
            $"/work-items/re-accreditation/{s_workItemId}/prior-year",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(logs.Records, r => r.Level == LogLevel.Error);
        await reEx
            .DidNotReceive()
            .GetPriorYearAsync(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Theory]
    // A write route on the readable work item: refused by the default policy.
    [InlineData("POST", "/work-items/{0}/assign", HttpStatusCode.Forbidden)]
    [InlineData("POST", "/work-items/{0}/notes", HttpStatusCode.Forbidden)]
    // Another method on an opened read route: the path has only a GET
    // endpoint, so routing answers 405 and no handler or policy runs.
    [InlineData("PUT", "/work-items/{0}", HttpStatusCode.MethodNotAllowed)]
    [InlineData("DELETE", "/work-items/{0}", HttpStatusCode.MethodNotAllowed)]
    [InlineData("POST", "/work-items/re-accreditation/{0}/prior-year", HttpStatusCode.MethodNotAllowed)]
    public async Task Admin_ui_cannot_write_to_a_work_item_it_can_read(
        string method,
        string template,
        HttpStatusCode expected
    )
    {
        var (factory, app, _) = NewApp();
        await using var _f = factory;
        await using var _a = app;
        factory.MockPersistence.GetByIdAsync(s_workItemId, Arg.Any<CancellationToken>()).Returns(StoredWorkItem());
        using var client = AdminClient(factory, app, $"nonce-admin-write-{method}-{template.GetHashCode()}");
        using var request = new HttpRequestMessage(new HttpMethod(method), string.Format(template, s_workItemId))
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
        // No handler ran: the work item was neither read nor written.
        await factory.MockPersistence.DidNotReceive().GetByIdAsync(s_workItemId, Arg.Any<CancellationToken>());
        await factory
            .MockPersistence.DidNotReceive()
            .ReplaceAsync(Arg.Is<WorkItem>(w => w.Id == s_workItemId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Management_fe_is_not_restricted_by_the_read_only_policy()
    {
        var (factory, app, logs) = NewApp();
        await using var _f = factory;
        await using var _a = app;
        using var client = SignedClient(factory, app, "frontend", ManagementFeSecret, "nonce-fe-get");

        var response = await client.GetAsync($"/work-items/{s_workItemId}", TestContext.Current.CancellationToken);

        // Not 403: the item simply does not exist in the empty mock store.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(logs.Records, r => r.Message.Contains("Read-only caller", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Admin_ui_signed_with_another_callers_secret_is_rejected_with_401()
    {
        var (factory, app, _) = NewApp();
        await using var _f = factory;
        await using var _a = app;
        using var client = SignedClient(
            factory,
            app,
            CallerAccessPolicies.AdminUiClientIdDefault,
            ManagementFeSecret,
            "nonce-admin-wrong-secret"
        );

        var response = await client.GetAsync("/work-items", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_ui_is_rejected_with_401_when_its_secret_is_not_configured()
    {
        var (factory, app, _) = NewApp(adminSecret: null);
        await using var _f = factory;
        await using var _a = app;
        using var client = AdminClient(factory, app, "nonce-admin-unregistered");

        var response = await client.GetAsync("/work-items", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_ui_with_no_signature_is_rejected_with_401()
    {
        var (factory, app, _) = NewApp();
        await using var _f = factory;
        await using var _a = app;
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, CallerAccessPolicies.AdminUiClientIdDefault);

        var response = await client.GetAsync("/work-items", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_ui_stays_read_only_in_development_header_trust_mode()
    {
        // No secrets at all: Development trusts the headers, but the Admin
        // UI's client id is still read-only.
        var factory = new BareFactory(environment: "Development");
        factory
            .MockPersistence.QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(Array.Empty<WorkItem>(), 0, 1, 50));
        await using var _f = factory;
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, CallerAccessPolicies.AdminUiClientIdDefault);
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultUserIdHeaderName, "dev-user");

        var list = await client.GetAsync("/work-items", TestContext.Current.CancellationToken);
        var assign = await client.PostAsync(
            $"/work-items/{s_workItemId}/assign",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, assign.StatusCode);
    }

    [Fact]
    public async Task Admin_ui_client_id_can_be_overridden_and_the_override_is_read_only()
    {
        var factory = new BareFactory(
            environment: "Production",
            configOverrides: new Dictionary<string, string?>
            {
                ["AUTH_SHARED_SECRET:ADMIN_UI"] = AdminSecret,
                [CallerAccessPolicies.AdminUiClientIdKey] = "admin-override",
            }
        );
        await using var _f = factory;
        using var client = SignedClient(factory, factory, "admin-override", AdminSecret, "nonce-admin-override");

        var response = await client.PostAsync(
            $"/work-items/{s_workItemId}/unassign",
            content: null,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static WorkItem ItemWithContactDetails(Guid? id = null) =>
        new()
        {
            Id = id ?? s_workItemId,
            TypeId = "re-accreditation",
            StateId = "submitted",
            SubmittedAt = new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc),
            LastModifiedAt = new DateTime(2026, 4, 2, 9, 0, 0, DateTimeKind.Utc),
            SubmittedBy = "operator-user-1",
            AssignedToId = "caseworker-1",
            AssignedToName = "Case Worker",
            Payload = new BsonDocument
            {
                ["applicationReference"] = "APEPL5005001",
                ["operatorOrganisationId"] = "org-1",
                ["organisationName"] = "Acme Plastics Ltd",
                ["operatorEmail"] = "contact@example.com",
                ["nation"] = "England",
            },
        };

    private static async Task<JsonElement> FirstListedItemAsync(HttpClient client)
    {
        var response = await client.GetAsync("/work-items", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken
        );
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("totalCount").GetInt64());
        return root.GetProperty("items")[0].Clone();
    }

    [Fact]
    public async Task Admin_ui_receives_only_the_projected_fields()
    {
        // RA-751 / ADR-0010: no submitter, no contact details, no other payload.
        var (factory, app, _) = NewApp();
        await using var _f = factory;
        await using var _a = app;
        factory
            .MockPersistence.QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage([ItemWithContactDetails()], 1, 1, 50));
        using var client = AdminClient(factory, app, "nonce-admin-projection");

        var item = await FirstListedItemAsync(client);

        Assert.Equal(
            ["assignedToId", "assignedToName", "id", "lastModifiedAt", "payload", "stateId", "typeId"],
            item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
        );
        var payload = item.GetProperty("payload");
        Assert.Equal(
            ["applicationReference", "operatorOrganisationId", "organisationName"],
            payload.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
        );
        Assert.Equal("APEPL5005001", payload.GetProperty("applicationReference").GetString());
        Assert.Equal("Acme Plastics Ltd", payload.GetProperty("organisationName").GetString());
        Assert.Equal("Case Worker", item.GetProperty("assignedToName").GetString());
    }

    [Fact]
    public async Task Admin_ui_projection_reports_an_absent_payload_field_as_null()
    {
        var (factory, app, _) = NewApp();
        await using var _f = factory;
        await using var _a = app;
        var legacy = ItemWithContactDetails();
        legacy.Payload.Remove("applicationReference");
        factory
            .MockPersistence.QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage([legacy], 1, 1, 50));
        using var client = AdminClient(factory, app, "nonce-admin-projection-null");

        var item = await FirstListedItemAsync(client);

        Assert.Equal(JsonValueKind.Null, item.GetProperty("payload").GetProperty("applicationReference").ValueKind);
    }

    [Fact]
    public async Task Admin_ui_still_gets_the_page_when_one_item_has_a_non_string_payload_field()
    {
        // One corrupt record must not make the whole results page a 500: its
        // bad field comes back null (the Admin UI shows "Missing from work
        // item") and is logged at Error; every other item is unaffected.
        var (factory, app, logs) = NewApp();
        await using var _f = factory;
        await using var _a = app;
        var corrupt = ItemWithContactDetails();
        corrupt.Payload["organisationName"] = new BsonInt32(42);
        var healthy = ItemWithContactDetails(Guid.Parse("00000751-0000-4000-8000-000000000002"));
        factory
            .MockPersistence.QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage([corrupt, healthy], 2, 1, 50));
        using var client = AdminClient(factory, app, "nonce-admin-projection-corrupt");

        var response = await client.GetAsync("/work-items", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken
        );
        var items = json.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        var corruptPayload = items[0].GetProperty("payload");
        Assert.Equal(JsonValueKind.Null, corruptPayload.GetProperty("organisationName").ValueKind);
        Assert.Equal("APEPL5005001", corruptPayload.GetProperty("applicationReference").GetString());
        Assert.Equal(
            "Acme Plastics Ltd",
            items[1].GetProperty("payload").GetProperty("organisationName").GetString()
        );
        Assert.Single(
            logs.Records,
            r =>
                r.Level == LogLevel.Error
                && r.Message.StartsWith("Work item payload field is not a string", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task Management_fe_still_receives_the_full_item()
    {
        var (factory, app, _) = NewApp();
        await using var _f = factory;
        await using var _a = app;
        factory
            .MockPersistence.QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage([ItemWithContactDetails()], 1, 1, 50));
        using var client = SignedClient(factory, app, "frontend", ManagementFeSecret, "nonce-fe-full");

        var item = await FirstListedItemAsync(client);

        Assert.Equal("operator-user-1", item.GetProperty("submittedBy").GetString());
        Assert.Equal("contact@example.com", item.GetProperty("payload").GetProperty("operatorEmail").GetString());
    }

    [Theory]
    [InlineData("frontend", null)]
    [InlineData("epr-register-enrol-backend", null)]
    [InlineData("frontend", AdminSecret)]
    public void Startup_fails_when_the_admin_ui_client_id_is_another_callers(string clientId, string? adminSecret)
    {
        // Whether or not the admin secret is set: the Admin UI's client id is
        // read-only regardless, so a collision would make that caller read-only.
        var overrides = new Dictionary<string, string?>
        {
            ["AUTH_SHARED_SECRET:MANAGEMENT_FE"] = ManagementFeSecret,
            [CallerAccessPolicies.AdminUiClientIdKey] = clientId,
        };
        if (adminSecret is not null)
        {
            overrides["AUTH_SHARED_SECRET:ADMIN_UI"] = adminSecret;
        }
        using var factory = new BareFactory(environment: "Production", configOverrides: overrides);

        var thrown = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var message = string.Join(" | ", Flatten(thrown).Select(e => e.Message));
        Assert.Contains($"{CallerAccessPolicies.AdminUiClientIdKey} is '{clientId}'", message);
        Assert.Contains("Give the Admin UI a distinct clientId", message);
    }

    private static IEnumerable<Exception> Flatten(Exception e)
    {
        yield return e;
        var inner = e is AggregateException agg ? agg.InnerExceptions : (e.InnerException is { } i ? [i] : []);
        foreach (var child in inner.SelectMany(Flatten))
        {
            yield return child;
        }
    }

    // The Admin UI sends x-cdp-user-name as encodeURIComponent(name). An Entra
    // display name is at most 256 chars and a BMP character such as '王'
    // encodes to 9 ASCII chars (%E7%8E%8B), so the longest valid name arrives
    // as 2304 chars. Uri.EscapeDataString encodes '王' exactly as
    // encodeURIComponent does.
    [Fact]
    public async Task Admin_ui_is_accepted_with_the_longest_entra_name_percent_encoded()
    {
        var (factory, app, _) = NewApp();
        await using var _f = factory;
        await using var _a = app;
        var encoded = Uri.EscapeDataString(new string('王', 256));
        Assert.Equal(2304, encoded.Length);
        using var client = SignedClient(
            factory,
            app,
            CallerAccessPolicies.AdminUiClientIdDefault,
            AdminSecret,
            "nonce-admin-long-name",
            encoded
        );

        var response = await client.GetAsync("/work-items", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Admin_ui_is_refused_with_401_when_the_encoded_name_is_over_the_cap()
    {
        var (factory, app, _) = NewApp();
        await using var _f = factory;
        await using var _a = app;
        var encoded = Uri.EscapeDataString(new string('王', 257));
        using var client = SignedClient(
            factory,
            app,
            CallerAccessPolicies.AdminUiClientIdDefault,
            AdminSecret,
            "nonce-admin-too-long-name",
            encoded
        );

        var response = await client.GetAsync("/work-items", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            $"{ClientIdDefaults.DefaultUserNameHeaderName} exceeds 2304 chars",
            response.Headers.WwwAuthenticate.ToString()
        );
    }

    [Fact]
    public void Extended_signature_over_a_percent_encoded_name_matches_the_known_value()
    {
        // The Admin UI sends encodeURIComponent(name) and signs exactly that
        // header string; management-be verifies over the header as received.
        var signature = ClientIdAuthenticationHandler.ComputeSignature(
            "admin-test-secret",
            new ClientIdSignaturePayload(
                "epr-register-enrol-admin",
                "entra-oid-123",
                "Si%C3%A2n%20O%E2%80%99Brien",
                "2026-11-25T10:00:00.000Z",
                "nonce-abc",
                Role: SupportRole
            )
        );

        Assert.Equal("xUgQC3G8CMtms+/s3rWHQRZ5BAiunCjZgm9lI3muA/M=", signature);
    }
}
