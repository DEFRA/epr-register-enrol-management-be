using System.Net;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test;

public class SwaggerUiEndpointTests
{
    [Fact]
    public async Task Swagger_ui_index_is_served_in_test_environment_without_auth()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new SwaggerFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/index.html", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Swagger_ui_stub_user_picker_script_is_served_when_swagger_ui_is_enabled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new SwaggerFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger-ui-stub-users.js", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/javascript",
            response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(ct);
        // Sanity: contains the dropdown-mount marker and one of the stub
        // user ids embedded in the JS bundle, confirming the asset that
        // monkey-patches window.fetch with CDP trust headers is wired up.
        Assert.Contains("epr-stub-user-picker", body, StringComparison.Ordinal);
        Assert.Contains("stub-caseworker-1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Swagger_ui_is_disabled_in_production_by_default()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new ProductionSwaggerFactory();
        using var client = factory.CreateClient();

        // Hard AC from RA-124: Swagger UI must be disabled in Production
        // unless an operator explicitly opts in via Swagger:Enabled. Both
        // the explorer page and the stub-user picker JS asset must 404.
        var ui = await client.GetAsync("/swagger/index.html", ct);
        Assert.Equal(HttpStatusCode.NotFound, ui.StatusCode);

        var script = await client.GetAsync("/swagger-ui-stub-users.js", ct);
        Assert.Equal(HttpStatusCode.NotFound, script.StatusCode);
    }

    [Fact]
    public async Task Swagger_ui_can_be_opted_in_in_production_via_config_flag()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new ProductionSwaggerFactory(swaggerEnabled: true);
        using var client = factory.CreateClient();

        var ui = await client.GetAsync("/swagger/index.html", ct);
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
    }

    private class SwaggerFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMongoDbClientFactory>();
                services.AddSingleton(Substitute.For<IMongoDbClientFactory>());
            });
        }
    }

    private sealed class ProductionSwaggerFactory : SwaggerFactory
    {
        private readonly bool _swaggerEnabled;

        public ProductionSwaggerFactory(bool swaggerEnabled = false)
        {
            _swaggerEnabled = swaggerEnabled;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            if (_swaggerEnabled)
            {
                builder.UseSetting("Swagger:Enabled", "true");
            }
            base.ConfigureWebHost(builder);
        }
    }
}
