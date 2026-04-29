using System.Net;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.Auth;

public class CognitoClientIdAuthenticationTests
{
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
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", "upstream-service");

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
        await using var factory = new BareFactory(sharedSecret: "test-secret");
        factory.MockPersistence
            .QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(Array.Empty<WorkItem>(), 0, 1, 20));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", "upstream-service");

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signature_required_tampered_signature_is_401()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(sharedSecret: "test-secret");

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", "upstream-service");
        client.DefaultRequestHeaders.Add("x-cdp-auth-signature", "AAAAtampered==");

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signature_required_valid_signature_is_200()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(sharedSecret: "test-secret");
        factory.MockPersistence
            .QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(Array.Empty<WorkItem>(), 0, 1, 20));

        var signature = EprRegisterEnrolManagementBe.Auth.CognitoClientIdAuthenticationHandler
            .ComputeSignature("test-secret", "upstream-service", null, null, null);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", "upstream-service");
        client.DefaultRequestHeaders.Add("x-cdp-auth-signature", signature);

        var response = await client.GetAsync("/work-items", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class BareFactory(string? sharedSecret = null) : WebApplicationFactory<Program>
    {
        public readonly IWorkItemPersistence MockPersistence = Substitute.For<IWorkItemPersistence>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            if (sharedSecret is not null)
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Auth:SharedSecret"] = sharedSecret
                    });
                });
            }
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IWorkItemPersistence>();
                services.AddSingleton(MockPersistence);
            });
        }
    }
}
