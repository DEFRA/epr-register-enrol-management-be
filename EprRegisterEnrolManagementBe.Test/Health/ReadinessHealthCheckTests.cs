using System.Net;
using EprRegisterEnrolManagementBe.Health;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EprRegisterEnrolManagementBe.Test.Health;

public class ReadinessHealthCheckTests
{
    [Fact]
    public async Task Liveness_returns_healthy_even_when_mongo_is_down()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new HealthFactory(mongoHealthy: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_fails_when_mongo_factory_cannot_connect()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new HealthFactory(mongoHealthy: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_succeeds_when_mongo_factory_is_healthy()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new HealthFactory(mongoHealthy: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class HealthFactory(bool mongoHealthy) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMongoDbClientFactory>();
                var mongoFactory = Substitute.For<IMongoDbClientFactory>();
                var client = Substitute.For<IMongoClient>();
                var db = Substitute.For<IMongoDatabase>();
                client.GetDatabase("admin", Arg.Any<MongoDatabaseSettings?>()).Returns(db);
                if (mongoHealthy)
                {
                    db.RunCommandAsync(Arg.Any<Command<BsonDocument>>(),
                        Arg.Any<ReadPreference>(), Arg.Any<CancellationToken>())
                        .Returns(new BsonDocument("ok", 1));
                }
                else
                {
                    db.RunCommandAsync(Arg.Any<Command<BsonDocument>>(),
                        Arg.Any<ReadPreference>(), Arg.Any<CancellationToken>())
                        .Throws(new TimeoutException("mongo unreachable"));
                }
                mongoFactory.GetClient().Returns(client);
                services.AddSingleton(mongoFactory);
            });
        }
    }
}
