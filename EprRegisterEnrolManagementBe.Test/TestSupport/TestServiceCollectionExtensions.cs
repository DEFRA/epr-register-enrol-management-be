using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolManagementBe.Test.TestSupport;

/// <summary>
/// Helpers for swapping the production Mongo wiring in a
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>-
/// based test for the ephemeral mongod managed by
/// <see cref="MongoIntegrationFixture"/>. Centralised so endpoint
/// suites do not silently diverge in how they wire the DI container.
/// </summary>
public static class TestServiceCollectionExtensions
{
    /// <summary>
    /// Replace <see cref="IMongoDbClientFactory"/> and
    /// <see cref="IWorkItemPersistence"/> with implementations that
    /// talk to <paramref name="fixture"/>'s ephemeral mongod against
    /// a fresh per-test <paramref name="databaseName"/>. The
    /// production <see cref="WorkItemPersistence"/> class is used
    /// verbatim — only its connection target is swapped.
    /// </summary>
    public static IServiceCollection UseEphemeralMongoPersistence(
        this IServiceCollection services,
        MongoIntegrationFixture fixture,
        string databaseName)
    {
        services.RemoveAll<IWorkItemPersistence>();
        services.RemoveAll<IMongoDbClientFactory>();

        var clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, databaseName);
        services.AddSingleton<IMongoDbClientFactory>(clientFactory);
        services.AddSingleton<IWorkItemPersistence>(sp =>
            new WorkItemPersistence(clientFactory, sp.GetRequiredService<ILoggerFactory>()));

        return services;
    }
}
