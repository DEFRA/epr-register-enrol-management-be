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

    [Theory]
    [InlineData("x-cdp-cognito-client-id")]
    [InlineData("x-cdp-user-id")]
    [InlineData("x-cdp-user-name")]
    [InlineData("x-cdp-user-roles")]
    [InlineData("x-cdp-auth-signature")]
    [InlineData("x-cdp-auth-timestamp")]
    [InlineData("x-cdp-auth-nonce")]
    [InlineData("Authorization")]
    [InlineData("Cookie")]
    [InlineData("x-api-key")]
    public async Task Preflight_does_not_allow_x_cdp_identity_header(string forbiddenHeader)
    {
        // Defence-in-depth: these headers are BFF-injected server-side and the
        // HMAC signature check is the primary defence; CORS must not advertise
        // them to browsers. Authorization / Cookie are caller credentials we
        // never accept cross-origin (CDP traffic comes via the BFF).
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(allowedOrigin: "https://trusted.example.com");
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Options, "/health");
        request.Headers.Add("Origin", "https://trusted.example.com");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", forbiddenHeader);

        var response = await client.SendAsync(request, cancellationToken);

        var allowed = response.Headers.TryGetValues("Access-Control-Allow-Headers", out var values)
            ? string.Join(',', values)
            : string.Empty;
        Assert.DoesNotContain(forbiddenHeader, allowed, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Content-Type")]
    [InlineData("Accept")]
    [InlineData("traceparent")]
    [InlineData("tracestate")]
    [InlineData("x-request-id")]
    public async Task Preflight_allows_browser_legitimate_header(string allowedHeader)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(allowedOrigin: "https://trusted.example.com");
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Options, "/health");
        request.Headers.Add("Origin", "https://trusted.example.com");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", allowedHeader);

        var response = await client.SendAsync(request, cancellationToken);

        Assert.True(response.Headers.Contains("Access-Control-Allow-Headers"));
        var allowed = string.Join(',', response.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains(allowedHeader, allowed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preflight_allows_configured_trace_header()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(
            allowedOrigin: "https://trusted.example.com",
            traceHeader: "x-cdp-request-id");
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Options, "/health");
        request.Headers.Add("Origin", "https://trusted.example.com");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "x-cdp-request-id");

        var response = await client.SendAsync(request, cancellationToken);

        Assert.True(response.Headers.Contains("Access-Control-Allow-Headers"));
        var allowed = string.Join(',', response.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("x-cdp-request-id", allowed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preflight_still_allows_content_type()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new BareFactory(allowedOrigin: "https://trusted.example.com");
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Options, "/health");
        request.Headers.Add("Origin", "https://trusted.example.com");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "Content-Type");

        var response = await client.SendAsync(request, cancellationToken);

        Assert.True(response.Headers.Contains("Access-Control-Allow-Headers"));
        var allowed = string.Join(',', response.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("Content-Type", allowed, StringComparison.OrdinalIgnoreCase);
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

    private sealed class BareFactory(string? allowedOrigin = null, string? traceHeader = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            if (allowedOrigin is not null || traceHeader is not null)
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    var values = new Dictionary<string, string?>();
                    if (allowedOrigin is not null)
                    {
                        values["Cors:AllowedOrigins:0"] = allowedOrigin;
                    }
                    if (traceHeader is not null)
                    {
                        values["TraceHeader"] = traceHeader;
                    }
                    config.AddInMemoryCollection(values);
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