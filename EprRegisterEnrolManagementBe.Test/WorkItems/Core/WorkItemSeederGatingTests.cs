using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// epr-nqe: <see cref="WorkItemSeederHostedService"/> writes records that
/// reference stub user identifiers, so it must only be wired up when the
/// host is in Development AND <c>WorkItems:SeedOnStartup=true</c>. These
/// tests pin the gating contract so a misconfigured production deploy
/// cannot pollute the live Mongo collection.
///
/// The seed flag is supplied via <see cref="IWebHostBuilder.UseSetting"/>,
/// which feeds <c>WebApplicationBuilder.Configuration</c> before
/// <c>Program.ConfigureServices</c> runs. We deliberately do NOT mutate
/// the process-global <c>WorkItems__SeedOnStartup</c> environment
/// variable here: xUnit v3 parallelises test classes by default, and a
/// global mutation would race with any other test class spinning up a
/// <see cref="WebApplicationFactory{T}"/> at the same time and cause its
/// host to register the seeder, which would then fire
/// <c>QueryAsync(Page=1, PageSize=1)</c> against that test's mock
/// persistence. (Surfaced by epr-mf7 — CI was slow enough to overlap
/// these factories where local timing did not.)
/// </summary>
public class WorkItemSeederGatingTests : IClassFixture<MongoIntegrationFixture>
{
    private const string SeedFlagConfigKey = "WorkItems:SeedOnStartup";
    private readonly MongoIntegrationFixture _fixture;

    public WorkItemSeederGatingTests(MongoIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public void Seeder_is_not_registered_in_Production_even_when_flag_true()
    {
        using var factory = CreateFactory("Production", seedOnStartup: true);

        var hostedServices = factory.Services.GetServices<IHostedService>().ToList();

        Assert.DoesNotContain(hostedServices, s => s is WorkItemSeederHostedService);
        // The misconfiguration warning service IS registered so the
        // mistake surfaces in startup logs.
        Assert.Contains(hostedServices, s => s is WorkItemSeederMisconfigurationWarningHostedService);
    }

    [Fact]
    public void Seeder_is_registered_in_Development_when_flag_true()
    {
        using var factory = CreateFactory("Development", seedOnStartup: true);

        var hostedServices = factory.Services.GetServices<IHostedService>().ToList();

        Assert.Contains(hostedServices, s => s is WorkItemSeederHostedService);
        Assert.DoesNotContain(hostedServices, s => s is WorkItemSeederMisconfigurationWarningHostedService);
    }

    [Fact]
    public void Seeder_is_not_registered_in_Development_when_flag_false()
    {
        using var factory = CreateFactory("Development", seedOnStartup: false);

        var hostedServices = factory.Services.GetServices<IHostedService>().ToList();

        Assert.DoesNotContain(hostedServices, s => s is WorkItemSeederHostedService);
        Assert.DoesNotContain(hostedServices, s => s is WorkItemSeederMisconfigurationWarningHostedService);
    }

    private EphemeralMongoTestFactory CreateFactory(string environment, bool seedOnStartup) =>
        new(_fixture, "seeder-gating", environment, new Dictionary<string, string?>
        {
            [SeedFlagConfigKey] = seedOnStartup ? "true" : "false",
        });
}
