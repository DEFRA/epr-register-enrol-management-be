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
/// Configuration is supplied via the <c>WorkItems__SeedOnStartup</c>
/// environment variable rather than <c>ConfigureAppConfiguration</c>
/// because the latter only fires during <c>builder.Build()</c>, after
/// <c>Program.ConfigureServices</c> has already chosen which hosted
/// services to register. <see cref="WebApplicationFactory{T}"/> uses
/// the same env-var trick for <c>ASPNETCORE_ENVIRONMENT</c>.
/// </summary>
public class WorkItemSeederGatingTests
{
    private const string SeedFlagEnvVar = "WorkItems__SeedOnStartup";

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

    private static GatingFactory CreateFactory(string environment, bool seedOnStartup) =>
        new(environment, seedOnStartup);

    private sealed class GatingFactory : WebApplicationFactory<Program>
    {
        private readonly string _environment;
        private readonly string? _previousFlag;

        public GatingFactory(string environment, bool seedOnStartup)
        {
            _environment = environment;
            _previousFlag = Environment.GetEnvironmentVariable(SeedFlagEnvVar);
            Environment.SetEnvironmentVariable(SeedFlagEnvVar, seedOnStartup ? "true" : "false");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(_environment);
        }

        protected override void Dispose(bool disposing)
        {
            Environment.SetEnvironmentVariable(SeedFlagEnvVar, _previousFlag);
            base.Dispose(disposing);
        }
    }
}
