using System.Net;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test;

public class OpenApiEndpointTests
{
    [Fact]
    public async Task OpenApi_document_is_served_at_conventional_route_without_auth()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new OpenApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(body);

        // Document must be a valid OpenAPI doc and include at least one
        // of the existing endpoints (a work-item framework route under
        // /work-items is registered by MapWorkItemFrameworkEndpoints).
        Assert.True(document.RootElement.TryGetProperty("openapi", out _));
        Assert.True(document.RootElement.TryGetProperty("paths", out var paths));
        Assert.Equal(JsonValueKind.Object, paths.ValueKind);
        var pathNames = paths.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.NotEmpty(pathNames);
        Assert.Contains(pathNames, p => p.StartsWith("/work-items", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenApi_document_advertises_cdp_trust_headers_as_security_schemes_when_swagger_ui_is_enabled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new OpenApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(body);

        var schemes = document.RootElement
            .GetProperty("components")
            .GetProperty("securitySchemes");

        // All four CDP trust headers must be declared as apiKey-in-header
        // schemes so the Swagger UI explorer renders an Authorize button
        // for them. RA-124.
        var expected = new (string Scheme, string Header)[]
        {
            ("CognitoClientId", "x-cdp-cognito-client-id"),
            ("UserId", "x-cdp-user-id"),
            ("UserName", "x-cdp-user-name"),
            ("UserRoles", "x-cdp-user-roles"),
        };

        foreach (var (scheme, header) in expected)
        {
            var s = schemes.GetProperty(scheme);
            Assert.Equal("apiKey", s.GetProperty("type").GetString());
            Assert.Equal("header", s.GetProperty("in").GetString());
            Assert.Equal(header, s.GetProperty("name").GetString());
        }

        // Global security requirement so the headers are applied to every
        // operation.
        var security = document.RootElement.GetProperty("security");
        Assert.Equal(JsonValueKind.Array, security.ValueKind);
        Assert.True(security.GetArrayLength() >= 1);
    }

    private sealed class OpenApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                // OpenAPI generation does not need a live Mongo client, but
                // the host wires one up. Stub it so the factory boots.
                services.RemoveAll<IMongoDbClientFactory>();
                services.AddSingleton(Substitute.For<IMongoDbClientFactory>());
            });
        }
    }
}
