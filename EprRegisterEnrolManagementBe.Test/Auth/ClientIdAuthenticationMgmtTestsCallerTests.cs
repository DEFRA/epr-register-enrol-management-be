using System.Net;
using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.Auth;

/// <summary>
/// RA-000: the <c>epr-register-enrol-mgmt-tests</c> caller is registered
/// through Program.cs::BuildClientSecrets only when BOTH
/// <c>AUTH_SHARED_SECRET__MGMT_TESTS</c> is set AND
/// <c>TestOrganisations__Enabled</c> is true, and signs with the same v3 HMAC
/// as every other caller. Exercised via config KEYS (colon form), like the
/// other BuildClientSecrets tests in <see cref="ClientIdAuthenticationTests"/>.
/// </summary>
public class ClientIdAuthenticationMgmtTestsCallerTests
{
    private const string MgmtTestsClientId = "epr-register-enrol-mgmt-tests";
    private const string MgmtTestsSecret = "mgmt-tests-secret";
    private const string ManagementFeSecret = "management-fe-secret";

    private static BareFactory NewFactory(
        string environment,
        bool testOrganisations,
        string? mgmtTestsSecret,
        string? managementFeSecret = null
    )
    {
        var overrides = new Dictionary<string, string?>
        {
            ["TestOrganisations:Enabled"] = testOrganisations ? "true" : "false",
        };
        if (mgmtTestsSecret is not null)
        {
            overrides["AUTH_SHARED_SECRET:MGMT_TESTS"] = mgmtTestsSecret;
        }
        if (managementFeSecret is not null)
        {
            overrides["AUTH_SHARED_SECRET:MANAGEMENT_FE"] = managementFeSecret;
        }
        var factory = new BareFactory(environment: environment, configOverrides: overrides);
        factory
            .MockPersistence.QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(Array.Empty<WorkItem>(), 0, 1, 20));
        return factory;
    }

    private static HttpClient SignedClient(BareFactory factory, string secret, string nonce)
    {
        var timestamp = factory.FakeTime.GetUtcNow().ToString("O");
        var signature = ClientIdAuthenticationHandler.ComputeSignature(
            secret,
            new ClientIdSignaturePayload(MgmtTestsClientId, "e2e-user", "E2E User", timestamp, nonce)
        );
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, MgmtTestsClientId);
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultUserIdHeaderName, "e2e-user");
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultUserNameHeaderName, "E2E User");
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultTimestampHeaderName, timestamp);
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultNonceHeaderName, nonce);
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultSignatureHeaderName, signature);
        return client;
    }

    [Fact]
    public async Task Signed_request_succeeds_when_the_secret_is_set_and_test_organisations_are_enabled()
    {
        // Production host: fail-closed, so 200 is only possible if the
        // mgmt-tests secret genuinely registered and verified.
        await using var factory = NewFactory("Production", testOrganisations: true, MgmtTestsSecret);
        using var client = SignedClient(factory, MgmtTestsSecret, "nonce-mgmt-tests-ok");

        var response = await client.GetAsync("/work-items", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Signed_request_is_rejected_when_test_organisations_are_disabled()
    {
        // management-fe's secret keeps ClientSecrets non-empty, so the 401
        // comes from mgmt-tests being an unregistered client id — not from
        // the "no secrets at all" fail-closed branch.
        await using var factory = NewFactory(
            "Production",
            testOrganisations: false,
            MgmtTestsSecret,
            ManagementFeSecret
        );
        using var client = SignedClient(factory, MgmtTestsSecret, "nonce-mgmt-tests-off");

        var response = await client.GetAsync("/work-items", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signature_made_with_another_callers_secret_is_rejected()
    {
        await using var factory = NewFactory(
            "Production",
            testOrganisations: true,
            MgmtTestsSecret,
            ManagementFeSecret
        );
        using var client = SignedClient(factory, ManagementFeSecret, "nonce-mgmt-tests-wrong");

        var response = await client.GetAsync("/work-items", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The compose / dotnet-run stack runs Development with no caller
    /// secrets, i.e. header-trust. Half-configuring the mgmt-tests caller
    /// (secret without the switch, or the switch without the secret) must
    /// register nothing and so leave that behaviour exactly as it was.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Development_header_trust_is_unchanged_when_the_caller_is_half_configured(
        bool secretSet,
        bool testOrganisations
    )
    {
        await using var factory = NewFactory(
            "Development",
            testOrganisations,
            secretSet ? MgmtTestsSecret : null
        );
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, MgmtTestsClientId);

        var response = await client.GetAsync("/work-items", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
