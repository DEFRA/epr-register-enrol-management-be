using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Health;
using EprRegisterEnrolManagementBe.Utils;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.Utils.Http;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using System.Diagnostics.CodeAnalysis;
using EprRegisterEnrolManagementBe.Utils.Logging;
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
    // In-memory cache backs the HMAC nonce replay defence in
    // CognitoClientIdAuthenticationHandler. Singleton by default.
    services.AddMemoryCache();

    ConfigureAuth(services, configuration);

    ConfigureHeaderPropagation(services, configuration);
    ConfigureHttpClients(services);
    ConfigureMongo(services, configuration);
    ConfigureCors(services, configuration);

    services.AddHealthChecks()
        // Tagged "ready" so liveness (/health) sees no checks while
        // readiness (/health/ready) only fires checks tagged "ready".
        .AddCheck<MongoHealthCheck>("mongodb", tags: ["ready"]);

    ConfigureWorkItems(services);
}

[ExcludeFromCodeCoverage]
static void ConfigureWorkItems(IServiceCollection services)
{
    // Register the framework, then add one line per work item module.
    // See docs in EprRegisterEnrolManagementBe/WorkItems/Core for the contract a module must implement.
    services.AddWorkItemFramework();
    services.AddSingleton<IWorkItemPersistence, WorkItemPersistence>();
    services.AddWorkItemModule<ReAccreditationModule>();
}

[ExcludeFromCodeCoverage]
static void ConfigureAuth(IServiceCollection services, IConfiguration configuration)
{
    services
        .AddAuthentication(CognitoClientIdDefaults.AuthenticationScheme)
        .AddCognitoClientId();

    // Bind options lazily via PostConfigure so test fixtures that add
    // Auth:SharedSecret via WebApplicationFactory.ConfigureAppConfiguration
    // (which fires during builder.Build(), after this method runs) can still
    // override the value.
    services.AddOptions<CognitoClientIdAuthenticationOptions>(CognitoClientIdDefaults.AuthenticationScheme)
        .Configure<IConfiguration>((options, config) =>
            options.SharedSecret = config.GetValue<string>("Auth:SharedSecret"));

    services.AddAuthorization();
}

[ExcludeFromCodeCoverage]
static void ConfigureHeaderPropagation(IServiceCollection services, IConfiguration configuration)
{
    var traceHeader = configuration.GetValue<string>("TraceHeader");

    services.AddHeaderPropagation(options =>
    {
        // EXPLICIT allow-list of headers that may be propagated from the
        // incoming request to outbound HttpClient calls. This is a security
        // boundary: anything NOT listed here is dropped on the way out so a
        // crafted upstream request can't leak credentials or auth state to
        // downstream services.
        //
        // Things deliberately NOT propagated:
        //   * Authorization, Cookie  — caller credentials must never be
        //     replayed verbatim to a downstream API.
        //   * x-cdp-auth-signature, x-cdp-auth-timestamp, x-cdp-auth-nonce,
        //     x-api-key — caller-bound to THIS request: the signature is an
        //     HMAC over the trust headers for THIS API, the timestamp
        //     bounds replay against THIS API's clock, and the nonce is
        //     burned in THIS API's replay cache. Forwarding any of them
        //     downstream would either leak the integrity proof to another
        //     service or replay the nonce out of band.
        //   * x-cdp-user-id, x-cdp-user-name, x-cdp-user-roles,
        //     x-cdp-cognito-client-id — identity headers from the BFF are
        //     for THIS service to consume; downstream services that need
        //     them must mint their own.
        //
        // To add a new propagated header, add it here AND document why it
        // is safe to forward.
        if (!string.IsNullOrWhiteSpace(traceHeader))
        {
            // Distributed tracing correlation id. Safe: it carries no
            // authority and is the whole point of header propagation.
            options.Headers.Add(traceHeader);
        }
        // Standard W3C trace context. Safe for the same reason as above.
        options.Headers.Add("traceparent");
        options.Headers.Add("tracestate");
        // Request-id used by some load balancers / API gateways. Safe.
        options.Headers.Add("x-request-id");
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
    WorkItemBsonRegistration.Register();

    services
        .AddOptions<MongoConfig>()
        .Bind(configuration.GetRequiredSection("Mongo"))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    services.AddSingleton<IMongoDbClientFactory, MongoDbClientFactory>();
}

/// <summary>
/// CORS posture: deny-all by default. The named policy <c>BackendCors</c> is
/// applied with an explicit allow-list of trusted origins read from
/// <c>Cors:AllowedOrigins</c>. With no configured origins the policy is
/// registered with an empty list which means <em>no</em> browser origin can
/// successfully call the API — desired in CDP where calls go through the
/// server-side BFF.
/// </summary>
[ExcludeFromCodeCoverage]
static void ConfigureCors(IServiceCollection services, IConfiguration configuration)
{
    services.AddCors();

    // Bind options lazily via PostConfigure so test fixtures that add
    // Cors:AllowedOrigins via WebApplicationFactory.ConfigureAppConfiguration
    // (which fires during builder.Build(), after this method runs) can still
    // override the value.
    services.AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>()
        .Configure<IConfiguration>((options, config) =>
        {
            var allowedOrigins = config.GetSection("Cors:AllowedOrigins")
                .Get<string[]>() ?? Array.Empty<string>();

            options.AddPolicy("BackendCors", policy =>
            {
                if (allowedOrigins.Length == 0)
                {
                    // Deny-all: no allowed origin and no wildcard. Browser
                    // requests from any origin will receive no CORS headers and
                    // will be blocked by the browser.
                    policy.WithOrigins().DisallowCredentials();
                    return;
                }
                policy.WithOrigins(allowedOrigins)
                      .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
                      .WithHeaders("Authorization", "Content-Type",
                          "x-cdp-cognito-client-id", "x-cdp-user-id",
                          "x-cdp-user-name", "x-cdp-user-roles",
                          "x-cdp-auth-signature", "x-cdp-auth-timestamp",
                          "x-cdp-auth-nonce")
                      .DisallowCredentials();
            });
        });
}

[ExcludeFromCodeCoverage]
static void ConfigureMiddleware(WebApplication app)
{
    // ExceptionHandler MUST be the very first middleware so unhandled
    // exceptions thrown anywhere downstream are caught and translated into
    // an RFC 7807 ProblemDetails response (configured via
    // services.AddProblemDetails()) instead of leaking stack traces or
    // returning a bare 500.
    app.UseExceptionHandler();
    // StatusCodePages turns plain status-only responses (e.g. a 404 from
    // routing) into ProblemDetails too, for a uniform error shape.
    app.UseStatusCodePages();

    app.UseSerilogRequestLogging();

    app.UseHeaderPropagation();

    // CORS must run before Auth so preflight (OPTIONS) responses are
    // produced for browser clients before authorisation kicks in.
    app.UseCors("BackendCors");

    app.UseAuthentication();
    app.UseAuthorization();
}

[ExcludeFromCodeCoverage]
static void ConfigureEndpoints(WebApplication app)
{
    // Liveness: trivial — answers "is the process up". No dependency
    // checks; if the process is alive enough to respond, it's alive.
    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        Predicate = _ => false
    }).AllowAnonymous();
    // Readiness: only the "ready"-tagged checks run (currently MongoDB).
    // CDP / Kubernetes uses this to decide when to send traffic.
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready")
    }).AllowAnonymous();

    app.MapWorkItemFrameworkEndpoints();
    app.MapWorkItemModules();
}