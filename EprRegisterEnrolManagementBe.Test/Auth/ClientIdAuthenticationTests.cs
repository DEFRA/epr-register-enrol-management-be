using System.Net;
using System.Net.Http.Headers;
using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.Auth;

public class ClientIdAuthenticationTests
{
    private const string ClientId = "upstream-service";
    private const string Secret = "test-secret";
    private const string OtherClientId = "other-upstream-service";
    private const string OtherSecret = "other-test-secret";

    [Fact]
    public async Task Protected_endpoint_returns_401_without_client_id_header()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_returns_401_with_empty_client_id_header()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, string.Empty);

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_succeeds_with_client_id_header()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory();
        factory.MockPersistence
            .QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(Array.Empty<WorkItem>(), 0, 1, 20));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_endpoint_is_anonymous()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Signature_required_when_shared_secret_configured_request_without_signature_is_401()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(clientSecrets: new Dictionary<string, string> { [ClientId] = Secret });
        factory.MockPersistence
            .QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(Array.Empty<WorkItem>(), 0, 1, 20));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
        AddTimestampAndNonce(client, factory.FakeTime.GetUtcNow().ToString("O"), "nonce-1");

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signature_required_tampered_signature_is_401()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(clientSecrets: new Dictionary<string, string> { [ClientId] = Secret });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
        AddTimestampAndNonce(client, factory.FakeTime.GetUtcNow().ToString("O"), "nonce-2");
        client.DefaultRequestHeaders.Add("x-cdp-auth-signature", "AAAAtampered==");

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signature_required_valid_signature_with_timestamp_and_nonce_is_200()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(clientSecrets: new Dictionary<string, string> { [ClientId] = Secret });
        factory.MockPersistence
            .QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(Array.Empty<WorkItem>(), 0, 1, 20));

        var timestamp = factory.FakeTime.GetUtcNow().ToString("O");
        var nonce = "nonce-valid";
        var signature = ClientIdAuthenticationHandler.ComputeSignature(
            Secret, ClientId, null, null, timestamp, nonce);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
        AddTimestampAndNonce(client, timestamp, nonce);
        client.DefaultRequestHeaders.Add("x-cdp-auth-signature", signature);

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Signature_signed_with_a_different_known_callers_secret_is_401()
    {
        // RA-345: the secret used to verify a request must be the one
        // registered for the clientId it asserts — signing with another
        // known caller's secret (but claiming this caller's clientId) must
        // not succeed, or a compromise of one caller's secret would let it
        // impersonate the other.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(clientSecrets: new Dictionary<string, string>
        {
            [ClientId] = Secret,
            [OtherClientId] = OtherSecret
        });

        var timestamp = factory.FakeTime.GetUtcNow().ToString("O");
        var nonce = "nonce-cross-client";
        var signature = ClientIdAuthenticationHandler.ComputeSignature(
            OtherSecret, ClientId, null, null, timestamp, nonce);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
        AddTimestampAndNonce(client, timestamp, nonce);
        client.DefaultRequestHeaders.Add("x-cdp-auth-signature", signature);

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signature_with_unrecognized_client_id_is_401()
    {
        // RA-345: a clientId with no registered secret must fail closed,
        // even if signed correctly with some other caller's secret.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(clientSecrets: new Dictionary<string, string>
        {
            [ClientId] = Secret
        });

        var timestamp = factory.FakeTime.GetUtcNow().ToString("O");
        var nonce = "nonce-unknown-client";
        var signature = ClientIdAuthenticationHandler.ComputeSignature(
            Secret, OtherClientId, null, null, timestamp, nonce);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, OtherClientId);
        AddTimestampAndNonce(client, timestamp, nonce);
        client.DefaultRequestHeaders.Add("x-cdp-auth-signature", signature);

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Colliding_caller_client_ids_fail_loudly_on_first_options_resolution()
    {
        // RA-345: Auth:ManagementFeClientId and Auth:BackendClientId must
        // resolve to distinct clientIds — a misconfiguration that points
        // both at the same value would let one caller's secret silently
        // overwrite the other's in the resolved ClientSecrets map
        // (Program.cs::BuildClientSecrets/AddCallerSecret). NOT a startup
        // check: ClientSecrets is bound lazily (see the comment on
        // ClientIdAuthenticationOptions.ClientSecrets binding in
        // Program.cs), so this throws on first options resolution — the
        // first request that hits the auth handler — not at app boot.
        // /health is anonymous and never resolves auth options, so this
        // misconfiguration passes health checks and looks like a green
        // deploy right up until the first real request 500s. This exercises
        // BuildClientSecrets' binding logic via config KEYS (colon form,
        // the same form IConfiguration exposes real "__"-separated env
        // vars under — see AUTH_SHARED_SECRET:MANAGEMENT_FE below) rather
        // than a hand-built ClientSecrets dictionary. It does NOT exercise
        // the actual EnvironmentVariablesConfigurationProvider "__" -> ":"
        // rewrite itself — see
        // ClientSecrets_bind_from_real_double_underscore_environment_variables
        // for that.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(configOverrides: new Dictionary<string, string?>
        {
            ["AUTH_SHARED_SECRET:MANAGEMENT_FE"] = Secret,
            ["AUTH_SHARED_SECRET:BACKEND"] = OtherSecret,
            ["Auth:BackendClientId"] = "frontend" // collides with ManagementFe's default clientId
        });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, "frontend");

        var response = await client.GetAsync("/work-items", cancellationToken);

        // Options resolution throws InvalidOperationException; the app's
        // global exception handler (ExceptionLoggingHandler) turns that
        // into a 500 ProblemDetails response rather than a bare 401 — the
        // point is this fails LOUDLY and distinctly from ordinary auth
        // failures, not that it 401s.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Each_callers_own_secret_independently_succeeds()
    {
        // RA-345: both known callers can authenticate concurrently, each
        // with its own secret — proves the secrets are independent, not
        // one shared value both callers happen to know.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(clientSecrets: new Dictionary<string, string>
        {
            [ClientId] = Secret,
            [OtherClientId] = OtherSecret
        });
        factory.MockPersistence
            .QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(Array.Empty<WorkItem>(), 0, 1, 20));

        var timestamp = factory.FakeTime.GetUtcNow().ToString("O");

        using var clientA = factory.CreateClient();
        var nonceA = "nonce-caller-a";
        var signatureA = ClientIdAuthenticationHandler.ComputeSignature(
            Secret, ClientId, null, null, timestamp, nonceA);
        clientA.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
        AddTimestampAndNonce(clientA, timestamp, nonceA);
        clientA.DefaultRequestHeaders.Add("x-cdp-auth-signature", signatureA);

        using var clientB = factory.CreateClient();
        var nonceB = "nonce-caller-b";
        var signatureB = ClientIdAuthenticationHandler.ComputeSignature(
            OtherSecret, OtherClientId, null, null, timestamp, nonceB);
        clientB.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, OtherClientId);
        AddTimestampAndNonce(clientB, timestamp, nonceB);
        clientB.DefaultRequestHeaders.Add("x-cdp-auth-signature", signatureB);

        var responseA = await clientA.GetAsync("/work-items", cancellationToken);
        var responseB = await clientB.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, responseA.StatusCode);
        Assert.Equal(HttpStatusCode.OK, responseB.StatusCode);
    }

    [Fact]
    public async Task Signature_required_missing_timestamp_is_401()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(clientSecrets: new Dictionary<string, string> { [ClientId] = Secret });

        var signature = ClientIdAuthenticationHandler.ComputeSignature(
            Secret, ClientId, null, null,
            factory.FakeTime.GetUtcNow().ToString("O"), "nonce-no-ts");

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
        client.DefaultRequestHeaders.Add("x-cdp-auth-nonce", "nonce-no-ts");
        client.DefaultRequestHeaders.Add("x-cdp-auth-signature", signature);

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signature_required_stale_timestamp_is_401()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(clientSecrets: new Dictionary<string, string> { [ClientId] = Secret });

        // Six minutes ago — outside the default 5-minute skew.
        var staleTimestamp = factory.FakeTime.GetUtcNow().AddMinutes(-6).ToString("O");
        var nonce = "nonce-stale";
        var signature = ClientIdAuthenticationHandler.ComputeSignature(
            Secret, ClientId, null, null, staleTimestamp, nonce);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
        AddTimestampAndNonce(client, staleTimestamp, nonce);
        client.DefaultRequestHeaders.Add("x-cdp-auth-signature", signature);

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signature_required_future_timestamp_is_401()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(clientSecrets: new Dictionary<string, string> { [ClientId] = Secret });

        // Six minutes ahead — skew check is bidirectional.
        var futureTimestamp = factory.FakeTime.GetUtcNow().AddMinutes(6).ToString("O");
        var nonce = "nonce-future";
        var signature = ClientIdAuthenticationHandler.ComputeSignature(
            Secret, ClientId, null, null, futureTimestamp, nonce);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
        AddTimestampAndNonce(client, futureTimestamp, nonce);
        client.DefaultRequestHeaders.Add("x-cdp-auth-signature", signature);

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signature_required_missing_nonce_is_401()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(clientSecrets: new Dictionary<string, string> { [ClientId] = Secret });

        var timestamp = factory.FakeTime.GetUtcNow().ToString("O");
        var signature = ClientIdAuthenticationHandler.ComputeSignature(
            Secret, ClientId, null, null, timestamp, "would-have-been-nonce");

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
        client.DefaultRequestHeaders.Add("x-cdp-auth-timestamp", timestamp);
        client.DefaultRequestHeaders.Add("x-cdp-auth-signature", signature);

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signature_required_replayed_nonce_is_401()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(clientSecrets: new Dictionary<string, string> { [ClientId] = Secret });
        factory.MockPersistence
            .QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(Array.Empty<WorkItem>(), 0, 1, 20));

        var timestamp = factory.FakeTime.GetUtcNow().ToString("O");
        var nonce = "nonce-replay";
        var signature = ClientIdAuthenticationHandler.ComputeSignature(
            Secret, ClientId, null, null, timestamp, nonce);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
        AddTimestampAndNonce(client, timestamp, nonce);
        client.DefaultRequestHeaders.Add("x-cdp-auth-signature", signature);

        var first = await client.GetAsync("/work-items", cancellationToken);
        var second = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task Production_environment_without_shared_secret_returns_401()
    {
        // Fail CLOSED: in any non-Development environment having no
        // client secrets configured means the integrity contract with the
        // BFF is broken. Header-trust mode must NOT be used.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(environment: "Production");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Development_environment_without_shared_secret_allows_header_only_request()
    {
        // Development ergonomics: BFF stub mode runs without a shared
        // secret. Existing header-trust behaviour is preserved.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(environment: "Development");
        factory.MockPersistence
            .QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(Array.Empty<WorkItem>(), 0, 1, 20));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static void AddTimestampAndNonce(HttpClient client, string timestamp, string nonce)
    {
        client.DefaultRequestHeaders.Add("x-cdp-auth-timestamp", timestamp);
        client.DefaultRequestHeaders.Add("x-cdp-auth-nonce", nonce);
    }

    [Theory]
    [InlineData(ClientIdDefaults.DefaultHeaderName, 256)]
    [InlineData("x-cdp-user-id", 128)]
    [InlineData("x-cdp-user-name", 256)]
    public async Task Identity_header_exceeding_cap_is_401_with_descriptive_reason(
        string header, int cap)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory();
        using var client = factory.CreateClient();

        var oversize = new string('a', cap + 1);
        if (header == ClientIdDefaults.DefaultHeaderName)
        {
            client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, oversize);
        }
        else
        {
            client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
            client.DefaultRequestHeaders.Add(header, oversize);
        }

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        Assert.Contains($"{header} exceeds {cap} chars", challenge);
    }

    [Theory]
    [InlineData("x-cdp-auth-timestamp", 64)]
    [InlineData("x-cdp-auth-nonce", 128)]
    [InlineData("x-cdp-auth-signature", 256)]
    public async Task Signed_mode_header_exceeding_cap_is_401_with_descriptive_reason(
        string header, int cap)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(clientSecrets: new Dictionary<string, string> { [ClientId] = Secret });

        var goodTimestamp = factory.FakeTime.GetUtcNow().ToString("O");
        var goodNonce = $"nonce-{header}";
        var goodSignature = ClientIdAuthenticationHandler.ComputeSignature(
            Secret, ClientId, null, null, goodTimestamp, goodNonce);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);

        var oversize = new string('a', cap + 1);
        client.DefaultRequestHeaders.Add(
            "x-cdp-auth-timestamp",
            header == "x-cdp-auth-timestamp" ? oversize : goodTimestamp);
        client.DefaultRequestHeaders.Add(
            "x-cdp-auth-nonce",
            header == "x-cdp-auth-nonce" ? oversize : goodNonce);
        client.DefaultRequestHeaders.Add(
            "x-cdp-auth-signature",
            header == "x-cdp-auth-signature" ? oversize : goodSignature);

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        Assert.Contains($"{header} exceeds {cap} chars", challenge);
    }

    [Fact]
    public async Task Oversize_signature_is_rejected_without_running_HMAC_compute()
    {
        // The signature cap fires BEFORE HMAC compute / FixedTimeEquals.
        // We assert the request fails and the cap-specific reason wins
        // over the generic "Invalid signature" reason — which is a
        // proxy assertion that the cap check ran first.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(clientSecrets: new Dictionary<string, string> { [ClientId] = Secret });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
        AddTimestampAndNonce(client, factory.FakeTime.GetUtcNow().ToString("O"), "nonce-big-sig");
        client.DefaultRequestHeaders.Add("x-cdp-auth-signature", new string('a', 257));

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        Assert.Contains("x-cdp-auth-signature exceeds 256 chars", challenge);
        Assert.DoesNotContain("Invalid x-cdp-auth-signature", challenge);
    }

    [Theory]
    [InlineData(ClientIdDefaults.DefaultHeaderName, 256)]
    [InlineData("x-cdp-user-id", 128)]
    [InlineData("x-cdp-user-name", 256)]
    public async Task Identity_header_at_exactly_cap_is_accepted(string header, int cap)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory();
        factory.MockPersistence
            .QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(Array.Empty<WorkItem>(), 0, 1, 20));

        using var client = factory.CreateClient();
        var atCap = new string('a', cap);
        if (header == ClientIdDefaults.DefaultHeaderName)
        {
            client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, atCap);
        }
        else
        {
            client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
            client.DefaultRequestHeaders.Add(header, atCap);
        }

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Nonce_at_exactly_cap_is_accepted_in_signed_mode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(clientSecrets: new Dictionary<string, string> { [ClientId] = Secret });
        factory.MockPersistence
            .QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(Array.Empty<WorkItem>(), 0, 1, 20));

        var timestamp = factory.FakeTime.GetUtcNow().ToString("O");
        var nonce = new string('n', 128);
        var signature = ClientIdAuthenticationHandler.ComputeSignature(
            Secret, ClientId, null, null, timestamp, nonce);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, ClientId);
        AddTimestampAndNonce(client, timestamp, nonce);
        client.DefaultRequestHeaders.Add("x-cdp-auth-signature", signature);

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

/// <summary>
/// RA-345 regression: proves AUTH_SHARED_SECRET__MANAGEMENT_FE /
/// AUTH_SHARED_SECRET__BACKEND actually bind through the REAL
/// EnvironmentVariablesConfigurationProvider (which rewrites "__" to ":"
/// while loading), not just through a hand-built config-key dictionary.
/// A prior version of BuildClientSecrets read these with the literal
/// double-underscore string as the GetValue key, which silently never
/// matched — ClientSecrets resolved empty and the service 401'd every
/// authenticated request in any real (env-var-driven) deployment. The
/// other tests in ClientIdAuthenticationTests use
/// PostConfigure/config-key overrides that bypass this provider entirely,
/// so none of them would have caught that. Mutates process-global env
/// vars, hence the disabled-parallelization collection (same reasoning as
/// NotifyApiKeyEnvVarRegistrationTests).
/// </summary>
[Collection(EprRegisterEnrolManagementBe.Test.Notifications.EnvVarMutationCollection.Name)]
public class ClientIdAuthenticationEnvVarTests
{
    private const string ManagementFeSecret = "management-fe-env-secret";
    private const string BackendSecret = "backend-env-secret";

    [Fact]
    public async Task ClientSecrets_bind_from_real_double_underscore_environment_variables()
    {
        var previousFe = Environment.GetEnvironmentVariable("AUTH_SHARED_SECRET__MANAGEMENT_FE");
        var previousBackend = Environment.GetEnvironmentVariable("AUTH_SHARED_SECRET__BACKEND");
        try
        {
            Environment.SetEnvironmentVariable(
                "AUTH_SHARED_SECRET__MANAGEMENT_FE", ManagementFeSecret);
            Environment.SetEnvironmentVariable("AUTH_SHARED_SECRET__BACKEND", BackendSecret);

            // No configOverrides/clientSecrets bypass here — this factory
            // boots exactly as a real deployment would, reading whatever
            // Program.cs's real AddEnvironmentVariables()-backed
            // configuration resolves. environment MUST be non-Development:
            // if BuildClientSecrets silently resolves ClientSecrets empty
            // (the bug this test guards against), a Development host would
            // fall back to unsigned header-trust mode and this request
            // would still return 200 regardless of whether the signature
            // was ever checked — masking the exact failure this test
            // exists to catch. Production forces the fail-closed branch,
            // so 200 here is only possible if the secret genuinely bound.
            await using var factory = new BareFactory(environment: "Production");
            factory.MockPersistence
                .QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
                .Returns(new WorkItemPage(Array.Empty<WorkItem>(), 0, 1, 20));

            var timestamp = factory.FakeTime.GetUtcNow().ToString("O");
            var nonce = "nonce-real-env-var";
            // Default expected clientId for the ManagementFe slot (Program.cs).
            const string clientId = "frontend";
            var signature = ClientIdAuthenticationHandler.ComputeSignature(
                ManagementFeSecret, clientId, null, null, timestamp, nonce);

            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, clientId);
            client.DefaultRequestHeaders.Add("x-cdp-auth-timestamp", timestamp);
            client.DefaultRequestHeaders.Add("x-cdp-auth-nonce", nonce);
            client.DefaultRequestHeaders.Add("x-cdp-auth-signature", signature);

            var response = await client.GetAsync(
                "/work-items", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "AUTH_SHARED_SECRET__MANAGEMENT_FE", previousFe);
            Environment.SetEnvironmentVariable("AUTH_SHARED_SECRET__BACKEND", previousBackend);
        }
    }
}

internal sealed class BareFactory(
    IReadOnlyDictionary<string, string>? clientSecrets = null,
    string? environment = null,
    IReadOnlyDictionary<string, string?>? configOverrides = null)
    : WebApplicationFactory<Program>
{
    public readonly IWorkItemPersistence MockPersistence = Substitute.For<IWorkItemPersistence>();
    public readonly FakeTimeProvider FakeTime = new(DateTimeOffset.Parse("2026-04-30T12:00:00Z"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (environment is not null)
        {
            builder.UseEnvironment(environment);
        }
        if (configOverrides is not null)
        {
            // Config-KEY overrides (colon form) — exercises
            // BuildClientSecrets' binding logic, but NOT the real
            // EnvironmentVariablesConfigurationProvider "__" -> ":"
            // rewrite itself. See
            // ClientIdAuthenticationEnvVarTests for that.
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(configOverrides));
        }
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IWorkItemPersistence>();
            services.AddSingleton(MockPersistence);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(FakeTime);

            if (clientSecrets is not null)
            {
                // Set the resolved option directly (after Program.cs's
                // own env-var-driven Configure<> runs) — the test
                // client ids don't necessarily match the real
                // management-fe/backend clientId defaults, so we
                // bypass env-var binding entirely here.
                services.PostConfigure<ClientIdAuthenticationOptions>(
                    ClientIdDefaults.AuthenticationScheme,
                    o => o.ClientSecrets = clientSecrets);
            }
        });
    }
}
