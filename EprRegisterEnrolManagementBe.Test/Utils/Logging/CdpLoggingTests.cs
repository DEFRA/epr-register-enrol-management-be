using System.Security.Claims;
using EprRegisterEnrolManagementBe.Utils.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace EprRegisterEnrolManagementBe.Test.Utils.Logging;

/// <summary>
/// Regression coverage for epr-3yv. The original
/// <see cref="CdpLogging.Configuration(HostBuilderContext, IServiceProvider, LoggerConfiguration)"/>
/// resolved the <see cref="IHttpContextAccessor"/> via
/// <c>IConfiguration.Get&lt;HttpContextAccessor&gt;()</c>, which binds
/// configuration onto a brand-new instance instead of pulling the live
/// accessor out of DI. That made the ECS HTTP enricher silently
/// inactive. These tests pin the contract that the configured pipeline
/// pulls the accessor from the supplied service provider.
/// </summary>
public class CdpLoggingTests
{
    [Fact]
    public void Configuration_resolves_HttpContextAccessor_from_di()
    {
        var liveAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var provider = new TrackingServiceProvider(
            new ServiceCollection()
                .AddSingleton<IHttpContextAccessor>(liveAccessor)
                .BuildServiceProvider()
        );

        var ctx = NewHostBuilderContext();

        CdpLogging.Configuration(ctx, provider, new LoggerConfiguration());

        Assert.Contains(typeof(IHttpContextAccessor), provider.RequestedServices);
    }

    [Fact]
    public void Configuration_throws_when_HttpContextAccessor_is_not_registered()
    {
        // Asserting that a missing DI registration throws makes the
        // original bug impossible to silently restore: the only way
        // to satisfy this test is for the accessor to come from DI
        // (configuration binding produces a fresh non-null instance
        // and would NOT throw).
        var emptyProvider = new ServiceCollection().BuildServiceProvider();
        var ctx = NewHostBuilderContext();

        Assert.Throws<InvalidOperationException>(() =>
            CdpLogging.Configuration(ctx, emptyProvider, new LoggerConfiguration())
        );
    }

    /// <summary>
    /// Pins that <see cref="PiiRedactionEnricher"/> is actually wired into
    /// the real <c>mainLogger</c> sub-pipeline this method builds internally
    /// (not just correct in isolation, as <c>PiiRedactionEnricherTests</c>
    /// already covers) - proves an email claim never reaches this method's
    /// real composed output, not just that the standalone enricher redacts
    /// it when called directly.
    /// </summary>
    [Fact]
    public void Configuration_RedactsEmailClaim_InComposedPipeline()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Email, "person@example.com")],
            "TestAuth"
        );
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var provider = new ServiceCollection()
            .AddSingleton<IHttpContextAccessor>(
                new HttpContextAccessor { HttpContext = httpContext }
            )
            .BuildServiceProvider();

        var originalOut = Console.Out;
        var capturedOutput = new StringWriter();
        Console.SetOut(capturedOutput);
        try
        {
            var ctx = NewHostBuilderContext(withConsoleSink: true);
            var config = new LoggerConfiguration();

            CdpLogging.Configuration(ctx, provider, config);
            using var logger = config.CreateLogger();
            logger.Information("Notify send starting");
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = capturedOutput.ToString();
        Assert.Contains(PiiRedactionEnricher.RedactedValue, output);
        Assert.DoesNotContain("person@example.com", output);
    }

    private static HostBuilderContext NewHostBuilderContext(bool withConsoleSink = false) =>
        new(new Dictionary<object, object>())
        {
            HostingEnvironment = new TestHostEnvironment(),
            Configuration = withConsoleSink
                ? new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["Serilog:WriteTo:0:Name"] = "Console",
                            ["Serilog:WriteTo:0:Args:formatter"] =
                                "Elastic.CommonSchema.Serilog.EcsTextFormatter, Elastic.CommonSchema.Serilog",
                        }
                    )
                    .Build()
                : new ConfigurationBuilder().Build(),
        };

    /// <summary>
    /// Wraps a real <see cref="IServiceProvider"/> and records the set
    /// of service types that were resolved through it. Used to assert
    /// that <see cref="CdpLogging.Configuration"/> goes through DI
    /// rather than binding from <see cref="IConfiguration"/>.
    /// </summary>
    private sealed class TrackingServiceProvider : IServiceProvider
    {
        private readonly IServiceProvider _inner;

        public TrackingServiceProvider(IServiceProvider inner) => _inner = inner;

        public HashSet<Type> RequestedServices { get; } = new();

        public object? GetService(Type serviceType)
        {
            RequestedServices.Add(serviceType);
            return _inner.GetService(serviceType);
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "test";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
