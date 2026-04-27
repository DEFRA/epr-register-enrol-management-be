using System.Net;
using System.Net.Http.Json;
using Backend.Api.Example.Models;
using Backend.Api.Example.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Backend.Api.Test.Auth;

public class CognitoClientIdAuthenticationTests
{
    [Fact]
    public async Task Protected_endpoint_returns_401_without_client_id_header()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/example", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_returns_401_with_empty_client_id_header()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", string.Empty);

        var response = await client.GetAsync("/example", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_succeeds_with_client_id_header()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory();
        factory.MockPersistence
            .GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ExampleModel>().AsReadOnly());

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-cdp-cognito-client-id", "upstream-service");

        var response = await client.GetAsync("/example", cancellationToken);

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

    private sealed class BareFactory : WebApplicationFactory<Program>
    {
        public readonly IExamplePersistence MockPersistence = Substitute.For<IExamplePersistence>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IExamplePersistence>();
                services.AddSingleton(MockPersistence);
            });
        }
    }
}
