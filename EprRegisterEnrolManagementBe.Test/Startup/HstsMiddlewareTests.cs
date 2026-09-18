using System.Net;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.Startup;

// RA-463: UseHsts only emits Strict-Transport-Security when it believes the request arrived
// over HTTPS, and it's skipped entirely in Development (see Program.cs), so this needs its
// own Production-environment factory - the default WebApplicationFactory environment can't
// observe this header at all.
public class HstsMiddlewareTests
{
    [Fact]
    public async Task Response_IncludesStrictTransportSecurity_WhenRequestIsHttpsInProduction()
    {
        var ct = TestContext.Current.CancellationToken;
        // HstsMiddleware excludes "localhost" (and other loopback hosts) by default, which is
        // WebApplicationFactory's default BaseAddress - so the host must be overridden too, not
        // just the scheme, or the header is silently skipped for a reason unrelated to this test.
        await using var factory = new ProductionFactory();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://app.test") }
        );

        var response = await client.GetAsync("/health", ct);

        response.Headers.TryGetValues("Strict-Transport-Security", out var values);
        Assert.NotNull(values);
        Assert.Contains(values, v => v.Contains("max-age="));
    }

    [Fact]
    public async Task Response_OmitsStrictTransportSecurity_WhenRequestIsHttpInProduction()
    {
        var ct = TestContext.Current.CancellationToken;
        // TLS terminates upstream in CDP: without UseForwardedHeaders picking up
        // X-Forwarded-Proto ahead of UseHsts, every request reaches this middleware as plain
        // HTTP and UseHsts silently no-ops - this is exactly the failure mode flagged in PR
        // #193, so it's worth pinning down as a test rather than only a comment.
        await using var factory = new ProductionFactory();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://app.test") }
        );

        var response = await client.GetAsync("/health", ct);

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Response_IncludesStrictTransportSecurity_WhenForwardedProtoIsHttps()
    {
        var ct = TestContext.Current.CancellationToken;
        // Simulates the real deployed path: the connection to the container is plain HTTP,
        // but CDP's proxy sets X-Forwarded-Proto: https. UseForwardedHeaders must translate
        // that into Request.IsHttps before UseHsts runs, or the header is never emitted even
        // though CDP terminated TLS correctly.
        await using var factory = new ProductionFactory();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://app.test") }
        );
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Forwarded-Proto", "https");

        var response = await client.SendAsync(request, ct);

        response.Headers.TryGetValues("Strict-Transport-Security", out var values);
        Assert.NotNull(values);
        Assert.Contains(values, v => v.Contains("max-age="));
    }

    private sealed class ProductionFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMongoDbClientFactory>();
                var mongoFactory = Substitute.For<IMongoDbClientFactory>();
                var client = Substitute.For<IMongoClient>();
                var db = Substitute.For<IMongoDatabase>();
                client.GetDatabase("admin", Arg.Any<MongoDatabaseSettings?>()).Returns(db);
                db.RunCommandAsync(
                        Arg.Any<Command<BsonDocument>>(),
                        Arg.Any<ReadPreference>(),
                        Arg.Any<CancellationToken>()
                    )
                    .Returns(new BsonDocument("ok", 1));
                mongoFactory.GetClient().Returns(client);
                services.AddSingleton(mongoFactory);
            });
        }
    }
}
