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

public class CognitoClientIdAuthenticationTests
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
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", string.Empty);

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
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", ClientId);

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
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", ClientId);
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
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", ClientId);
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
        var signature = CognitoClientIdAuthenticationHandler.ComputeSignature(
            Secret, ClientId, null, null, timestamp, nonce);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", ClientId);
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
        var signature = CognitoClientIdAuthenticationHandler.ComputeSignature(
            OtherSecret, ClientId, null, null, timestamp, nonce);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", ClientId);
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
        var signature = CognitoClientIdAuthenticationHandler.ComputeSignature(
            Secret, OtherClientId, null, null, timestamp, nonce);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", OtherClientId);
        AddTimestampAndNonce(client, timestamp, nonce);
        client.DefaultRequestHeaders.Add("x-cdp-auth-signature", signature);

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
        var signatureA = CognitoClientIdAuthenticationHandler.ComputeSignature(
            Secret, ClientId, null, null, timestamp, nonceA);
        clientA.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", ClientId);
        AddTimestampAndNonce(clientA, timestamp, nonceA);
        clientA.DefaultRequestHeaders.Add("x-cdp-auth-signature", signatureA);

        using var clientB = factory.CreateClient();
        var nonceB = "nonce-caller-b";
        var signatureB = CognitoClientIdAuthenticationHandler.ComputeSignature(
            OtherSecret, OtherClientId, null, null, timestamp, nonceB);
        clientB.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", OtherClientId);
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

        var signature = CognitoClientIdAuthenticationHandler.ComputeSignature(
            Secret, ClientId, null, null,
            factory.FakeTime.GetUtcNow().ToString("O"), "nonce-no-ts");

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", ClientId);
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
        var signature = CognitoClientIdAuthenticationHandler.ComputeSignature(
            Secret, ClientId, null, null, staleTimestamp, nonce);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", ClientId);
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
        var signature = CognitoClientIdAuthenticationHandler.ComputeSignature(
            Secret, ClientId, null, null, futureTimestamp, nonce);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", ClientId);
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
        var signature = CognitoClientIdAuthenticationHandler.ComputeSignature(
            Secret, ClientId, null, null, timestamp, "would-have-been-nonce");

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", ClientId);
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
        var signature = CognitoClientIdAuthenticationHandler.ComputeSignature(
            Secret, ClientId, null, null, timestamp, nonce);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", ClientId);
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
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", ClientId);

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
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", ClientId);

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static void AddTimestampAndNonce(HttpClient client, string timestamp, string nonce)
    {
        client.DefaultRequestHeaders.Add("x-cdp-auth-timestamp", timestamp);
        client.DefaultRequestHeaders.Add("x-cdp-auth-nonce", nonce);
    }

    [Theory]
    [InlineData("x-cdp-cognito-client-id", 256)]
    [InlineData("x-cdp-user-id", 128)]
    [InlineData("x-cdp-user-name", 256)]
    public async Task Identity_header_exceeding_cap_is_401_with_descriptive_reason(
        string header, int cap)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory();
        using var client = factory.CreateClient();

        var oversize = new string('a', cap + 1);
        if (header == "x-cdp-cognito-client-id")
        {
            client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", oversize);
        }
        else
        {
            client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", ClientId);
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
        var goodSignature = CognitoClientIdAuthenticationHandler.ComputeSignature(
            Secret, ClientId, null, null, goodTimestamp, goodNonce);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", ClientId);

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
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", ClientId);
        AddTimestampAndNonce(client, factory.FakeTime.GetUtcNow().ToString("O"), "nonce-big-sig");
        client.DefaultRequestHeaders.Add("x-cdp-auth-signature", new string('a', 257));

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        Assert.Contains("x-cdp-auth-signature exceeds 256 chars", challenge);
        Assert.DoesNotContain("Invalid x-cdp-auth-signature", challenge);
    }

    [Theory]
    [InlineData("x-cdp-cognito-client-id", 256)]
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
        if (header == "x-cdp-cognito-client-id")
        {
            client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", atCap);
        }
        else
        {
            client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", ClientId);
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
        var signature = CognitoClientIdAuthenticationHandler.ComputeSignature(
            Secret, ClientId, null, null, timestamp, nonce);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", ClientId);
        AddTimestampAndNonce(client, timestamp, nonce);
        client.DefaultRequestHeaders.Add("x-cdp-auth-signature", signature);

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class BareFactory(
        IReadOnlyDictionary<string, string>? clientSecrets = null, string? environment = null)
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
                    services.PostConfigure<CognitoClientIdAuthenticationOptions>(
                        CognitoClientIdDefaults.AuthenticationScheme,
                        o => o.ClientSecrets = clientSecrets);
                }
            });
        }
    }
}
