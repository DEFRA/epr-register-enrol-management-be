using System.Net;
using System.Text;
using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.Auth;

/// <summary>
/// RA-751 / ADR-0010: the Admin UI (<c>epr-register-enrol-admin</c>) is a
/// read-only caller. It authenticates with its own secret,
/// <c>AUTH_SHARED_SECRET__ADMIN_UI</c>, and may only GET (or HEAD) the
/// endpoints that opt in to <see cref="CallerAccessPolicies.ReadOnlyCallerAllowed"/>:
/// <c>GET /work-items</c> and <c>GET /work-items/re-accreditation/states</c>.
/// Every other request it makes is refused with 403 and logged at Error.
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
        string? adminSecret = AdminSecret
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
        string nonce
    )
    {
        var timestamp = factory.FakeTime.GetUtcNow().ToString("O");
        var signature = ClientIdAuthenticationHandler.ComputeSignature(
            secret,
            new ClientIdSignaturePayload(clientId, "entra-oid-123", "Support User", timestamp, nonce, Role: SupportRole)
        );
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, clientId);
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultUserIdHeaderName, "entra-oid-123");
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultUserNameHeaderName, "Support User");
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
            // A read the Admin UI has not been given (RA-752 is not built).
            { "GET", $"/work-items/{s_workItemId}" },
            { "GET", $"/work-items/re-accreditation/{s_workItemId}/recommendation" },
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
        if (method == "POST")
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
}
