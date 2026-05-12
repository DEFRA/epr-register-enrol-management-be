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

    private sealed class SwaggerFactory : WebApplicationFactory<Program>
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
}
