using Backend.Api.Auth;
using Backend.Api.Example.Endpoints;
using Backend.Api.Example.Services;
using Backend.Api.Config;
using Backend.Api.Utils;
using Backend.Api.WorkItems.Core;
using Backend.Api.WorkItems.ReAccreditation;
using Backend.Api.Utils.Http;
using Backend.Api.Utils.Mongo;
using System.Diagnostics.CodeAnalysis;
using Backend.Api.Utils.Logging;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using MongoDB.Driver;
using MongoDB.Driver.Authentication.AWS;
using Serilog;

var app = BuildApp(args);
await app.RunAsync();

[ExcludeFromCodeCoverage]
static WebApplication BuildApp(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);

    ConfigureHost(builder);
    ConfigureServices(builder);

    var app = builder.Build();

    ConfigureMiddleware(app);
    ConfigureEndpoints(app);

    return app;
}

[ExcludeFromCodeCoverage]
static void ConfigureHost(WebApplicationBuilder builder)
{
    builder.Host.UseSerilog(CdpLogging.Configuration);
}

[ExcludeFromCodeCoverage]
static void ConfigureServices(WebApplicationBuilder builder)
{
    var services = builder.Services;
    var configuration = builder.Configuration;

    // Trust material must be loaded before anything creates outbound connections.
    services.LoadCustomTrustStoreFromEnvironment();

    services.AddProblemDetails();
    services.AddValidation();

    services.AddHttpContextAccessor();

    ConfigureAuth(services);

    ConfigureHeaderPropagation(services, configuration);
    ConfigureHttpClients(services);
    ConfigureMongo(services, configuration);

    services.AddHealthChecks();

    // App services
    services.AddSingleton<IExamplePersistence, ExamplePersistence>();

    ConfigureWorkItems(services);
}

[ExcludeFromCodeCoverage]
static void ConfigureWorkItems(IServiceCollection services)
{
    // Register the framework, then add one line per work item module.
    // See docs in Backend.Api/WorkItems/Core for the contract a module must implement.
    services.AddWorkItemFramework();
    services.AddSingleton<IWorkItemPersistence, WorkItemPersistence>();
    services.AddWorkItemModule<ReAccreditationModule>();
}

[ExcludeFromCodeCoverage]
static void ConfigureAuth(IServiceCollection services)
{
    services
        .AddAuthentication(CognitoClientIdDefaults.AuthenticationScheme)
        .AddCognitoClientId();

    services.AddAuthorization();
}

[ExcludeFromCodeCoverage]
static void ConfigureHeaderPropagation(IServiceCollection services, IConfiguration configuration)
{
    var traceHeader = configuration.GetValue<string>("TraceHeader");

    services.AddHeaderPropagation(options =>
    {
        if (!string.IsNullOrWhiteSpace(traceHeader))
        {
            options.Headers.Add(traceHeader);
        }
    });
}

[ExcludeFromCodeCoverage]
static void ConfigureHttpClients(IServiceCollection services)
{
    services.AddTransient<ProxyHttpMessageHandler>();

    // services.AddHttpClientWithTracing<IExampleClient, ExampleClient>();
    // services.AddHttpClientWithProxy<IExternalClient, ExternalClient>();
}

[ExcludeFromCodeCoverage]
static void ConfigureMongo(IServiceCollection services, IConfiguration configuration)
{

    MongoExtensions.Register();
    MongoConventions.Register();

    services
        .AddOptions<MongoConfig>()
        .Bind(configuration.GetRequiredSection("Mongo"))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    services.AddSingleton<IMongoDbClientFactory, MongoDbClientFactory>();
}

[ExcludeFromCodeCoverage]
static void ConfigureMiddleware(WebApplication app)
{
    app.UseSerilogRequestLogging();

    app.UseHeaderPropagation();

    app.UseAuthentication();
    app.UseAuthorization();
}

[ExcludeFromCodeCoverage]
static void ConfigureEndpoints(WebApplication app)
{
    app.MapHealthChecks("/health", new HealthCheckOptions()).AllowAnonymous();

    // Remove before deploying
    app.MapExampleEndpoints().RequireAuthorization();

    app.MapWorkItemFrameworkEndpoints();
    app.MapWorkItemModules();
}