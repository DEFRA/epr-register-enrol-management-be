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

    [Theory]
    [InlineData("SubmitWorkItem")]
    [InlineData("SetWorkItemTaskStatus")]
    [InlineData("AssignWorkItem")]
    [InlineData("AddWorkItemNote")]
    [InlineData("RecordReAccreditationDecisionRationale")]
    public async Task Request_body_example_is_present_for_known_operation(string operationId)
    {
        // Drift guard for WorkItemOpenApiExampleTransformer: if an
        // endpoint's .WithName(...) is renamed without updating the
        // transformer's lookup table, the example silently disappears
        // from the rendered Swagger UI. Asserting at the document level
        // catches both the rename-only and the example-removal cases.
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new OpenApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(body);

        var found = false;
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var op in path.Value.EnumerateObject())
            {
                if (!op.Value.TryGetProperty("operationId", out var opIdEl) ||
                    opIdEl.GetString() != operationId)
                {
                    continue;
                }

                Assert.True(
                    op.Value.TryGetProperty("requestBody", out var requestBody),
                    $"Operation '{operationId}' must declare a requestBody.");
                var json = requestBody.GetProperty("content").GetProperty("application/json");
                Assert.True(
                    json.TryGetProperty("example", out _),
                    $"Operation '{operationId}' must have a request-body example wired " +
                    "in WorkItemOpenApiExampleTransformer.");
                found = true;
            }
        }

        Assert.True(found, $"Operation id '{operationId}' was not found in the OpenAPI document. " +
            "Either the endpoint was renamed or removed — update WorkItemOpenApiExampleTransformer " +
            "to match.");
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
