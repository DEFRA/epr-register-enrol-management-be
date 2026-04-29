using System.Net.Http;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.Auth;

public class CorsPolicyTests
{
    [Fact]
    public async Task Disallowed_origin_receives_no_CORS_headers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory();
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Origin", "https://evil.example.com");

        var response = await client.SendAsync(request, cancellationToken);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Allowed_origin_receives_matching_CORS_header()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(allowedOrigin: "https://trusted.example.com");
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Origin", "https://trusted.example.com");

        var response = await client.SendAsync(request, cancellationToken);

        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal("https://trusted.example.com",
            string.Join(',', response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    private sealed class BareFactory(string? allowedOrigin = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            if (allowedOrigin is not null)
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Cors:AllowedOrigins:0"] = allowedOrigin
                    });
                });
            }
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IWorkItemPersistence>();
                services.AddSingleton(Substitute.For<IWorkItemPersistence>());
            });
        }
    }
}
