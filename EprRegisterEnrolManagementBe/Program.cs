using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Health;
using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.Utils;
using EprRegisterEnrolManagementBe.Utils.Background;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.Utils.Http;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using System.Diagnostics.CodeAnalysis;
using EprRegisterEnrolManagementBe.Utils.Logging;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using MongoDB.Driver;
using MongoDB.Driver.Authentication.AWS;
using Notify.Client;
using Notify.Interfaces;
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

    services.AddExceptionHandler<ExceptionLoggingHandler>();
    services.AddProblemDetails();
    services.AddValidation();
    services.AddSingleton(TimeProvider.System);

    // Built-in .NET 10 OpenAPI document generation. Document only — no
    // Swagger UI is wired up (that ships in a separate package). The
    // document is exposed unauthenticated at /openapi/v1.json by
    // ConfigureEndpoints, mirroring the health endpoints' posture, since
    // BFF and platform tooling need to fetch it without credentials.
    services.AddOpenApi("v1", options =>
    {
        // Inject concrete request-body examples so the Swagger UI
        // "Try it out" panel pre-fills with real payloads. Runs last in
        // the transformer pipeline to survive schema population. RA-124.
        // AddDocumentTransformer<T> registers the transformer in DI, so
        // no separate AddSingleton call is required.
        options.AddDocumentTransformer<WorkItemOpenApiExampleTransformer>();
    });

    services.AddHttpContextAccessor();
    // In-memory cache backs the HMAC nonce replay defence in
    // CognitoClientIdAuthenticationHandler. Singleton by default.
    services.AddMemoryCache();

    ConfigureAuth(services, configuration);

    ConfigureHeaderPropagation(services, configuration);
    ConfigureHttpClients(services);
    ConfigureMongo(services, configuration);
    ConfigureCors(services, configuration);
    ConfigureNotifications(services, configuration);

    services.AddOptions<LivenessHealthCheckOptions>()
        .Bind(configuration.GetSection("Liveness"))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    services.AddHealthChecks()
        // Tagged "live" so liveness (/health) only fires checks tagged
        // "live" (currently the thread-pool probe), and readiness
        // (/health/ready) only fires checks tagged "ready". The two sets
        // are deliberately disjoint so a downed dependency cannot recycle
        // the pod and a wedged process cannot keep serving traffic.
        .AddCheck<LivenessHealthCheck>("threadpool", tags: ["live"])
        .AddCheck<MongoHealthCheck>("mongodb", tags: ["ready"]);

    ConfigureWorkItems(builder);

    // RA-132: in-process background-task queue + hosted worker. Used by
    // the re-accreditation approval service to fan out post-approval
    // side-effects (audit appends, future "publishing" events) on a
    // freshly-scoped service provider so HTTP request scope teardown
    // does not unwind them.
    services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
    services.AddHostedService<QueuedHostedService>();
}

[ExcludeFromCodeCoverage]
static void ConfigureWorkItems(WebApplicationBuilder builder)
{
    var services = builder.Services;

    // Register the framework, then add one line per work item module.
    // See docs in EprRegisterEnrolManagementBe/WorkItems/Core for the contract a module must implement.
    services.AddWorkItemFramework();
    services.AddSingleton<IWorkItemPersistence, WorkItemPersistence>();
    // RA-131: SLA extend / override is a cross-cutting framework concern
    // (universal rules: team-leader gate, max-extension cap, audit shape,
    // operator notify on extend via post-action hooks) so it lives next
    // to the framework, not in a module.
    services.AddOptions<SlaConfig>()
        .Bind(builder.Configuration.GetSection("WorkItems:Sla"));
    services.AddSingleton<ISlaService, SlaService>();
    services.AddWorkItemModule<ReAccreditationModule>();
    services.AddHostedService<SlaBreachBackgroundService>();
    services.AddHostedService<ArchiveBackgroundService>();

    // The seeder writes records referencing stub user ids, so it is
    // gated to Development hosts even when WorkItems:SeedOnStartup=true.
    // A misconfiguration warning is emitted at startup if the flag is
    // observed in any other environment. See
    // WorkItemModuleExtensions.AddWorkItemSeederIfDevelopment.
    services.AddWorkItemSeederIfDevelopment(builder.Environment, builder.Configuration);
}

[ExcludeFromCodeCoverage]
static void ConfigureAuth(IServiceCollection services, IConfiguration configuration)
{
    services
        .AddAuthentication(CognitoClientIdDefaults.AuthenticationScheme)
        .AddCognitoClientId();

    // Bind options lazily via PostConfigure so test fixtures that add
    // AUTH_SHARED_SECRET via WebApplicationFactory.ConfigureAppConfiguration
    // (which fires during builder.Build(), after this method runs) can still
    // override the value.
    services.AddOptions<CognitoClientIdAuthenticationOptions>(CognitoClientIdDefaults.AuthenticationScheme)
        .Configure<IConfiguration>((options, config) =>
        {
            options.SharedSecret = config.GetValue<string>("AUTH_SHARED_SECRET");
            options.MaxClientIdLength = config.GetValue("Auth:MaxClientIdLength", options.MaxClientIdLength);
            options.MaxUserIdLength = config.GetValue("Auth:MaxUserIdLength", options.MaxUserIdLength);
            options.MaxUserNameLength = config.GetValue("Auth:MaxUserNameLength", options.MaxUserNameLength);
            options.MaxUserRolesLength = config.GetValue("Auth:MaxUserRolesLength", options.MaxUserRolesLength);
            options.MaxSignatureLength = config.GetValue("Auth:MaxSignatureLength", options.MaxSignatureLength);
            options.MaxTimestampLength = config.GetValue("Auth:MaxTimestampLength", options.MaxTimestampLength);
            options.MaxNonceLength = config.GetValue("Auth:MaxNonceLength", options.MaxNonceLength);
        });

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
/// GOV.UK Notify wiring (RA-123). When <c>NOTIFY_API_KEY</c> is absent or
/// empty the no-op client is registered so the service still boots in
/// environments without Notify credentials. When set, the real
/// GovukNotify SDK client is registered behind the project-owned
/// <see cref="INotifyClient"/> abstraction with Polly retries.
/// </summary>
[ExcludeFromCodeCoverage]
static void ConfigureNotifications(IServiceCollection services, IConfiguration configuration)
{
    services.AddOptions<NotifyConfig>()
        .Bind(configuration.GetSection("Notify"));

    var apiKey = configuration.GetValue<string>("NOTIFY_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        services.AddSingleton<INotifyClient, NoOpNotifyClient>();
        return;
    }

    var baseUri = configuration.GetValue<string>("Notify:BaseUri");
    services.AddSingleton<IAsyncNotificationClient>(_ =>
        string.IsNullOrWhiteSpace(baseUri)
            ? new NotificationClient(apiKey)
            : new NotificationClient(baseUri, apiKey));
    services.AddSingleton<INotifyClient, GovukNotifyClient>();
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
            var traceHeader = config.GetValue<string>("TraceHeader");

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

                // EXPLICIT allow-list of request headers a browser is
                // permitted to send cross-origin. This is a security
                // boundary: a header that is NOT here will fail a CORS
                // preflight and the browser will refuse to issue the
                // request. Mirror of the propagation allow-list above —
                // keep them in sync.
                //
                // Things deliberately NOT advertised:
                //   * Authorization, Cookie  — we do not accept caller
                //     credentials over CORS. CDP traffic reaches this
                //     service via the server-side BFF, not directly from
                //     a browser.
                //   * x-cdp-auth-signature, x-cdp-auth-timestamp,
                //     x-cdp-auth-nonce, x-api-key — HMAC inputs are
                //     injected by the BFF server-side and must never
                //     originate from a browser. The HMAC check remains
                //     the primary defence; excluding them here ensures a
                //     browser preflight cannot even smuggle them.
                //   * x-cdp-user-id, x-cdp-user-name, x-cdp-user-roles,
                //     x-cdp-cognito-client-id — identity headers are
                //     BFF-injected and must not be browser-supplied.
                //
                // To advertise a new header, add it here AND document why
                // it is browser-legitimate.
                var allowedHeaders = new List<string>
                {
                    // Standard request payload negotiation.
                    "Content-Type",
                    "Accept",
                    // W3C trace context — same headers we propagate
                    // outbound. Carry no authority.
                    "traceparent",
                    "tracestate",
                    "x-request-id",
                };
                if (!string.IsNullOrWhiteSpace(traceHeader))
                {
                    allowedHeaders.Add(traceHeader);
                }

                policy.WithOrigins(allowedOrigins)
                      .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
                      .WithHeaders([.. allowedHeaders])
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
    // Liveness: only "live"-tagged checks run. Currently the thread-pool
    // probe (LivenessHealthCheck) which detects a wedged process — a
    // bare "answer 200 if the pipeline parsed the request" probe would
    // never let Kubernetes / CDP recycle a deadlocked or thread-pool-
    // starved pod.
    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("live")
    }).AllowAnonymous();
    // Readiness: only the "ready"-tagged checks run (currently MongoDB).
    // CDP / Kubernetes uses this to decide when to send traffic.
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready")
    }).AllowAnonymous();

    // OpenAPI document at the conventional /openapi/{documentName}.json
    // route. Anonymous on purpose: the BFF and platform tooling fetch it
    // unauthenticated, same posture as /health. Always-on (not gated on
    // IsDevelopment) because this service sits behind the BFF inside CDP
    // and the document is internal-platform-facing rather than public.
    app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();

    // Swagger UI explorer at /swagger, gated by SwaggerUiGating: enabled
    // outside Production, or anywhere that opts in via Swagger:Enabled.
    // Anonymous on purpose, mirroring the /health and /openapi posture —
    // the underlying document is already anonymous so authenticating the
    // explorer page would be theatre. Production stays off by default so
    // the explorer is not exposed to platform traffic without an explicit
    // operator decision.
    if (SwaggerUiGating.ShouldEnableSwaggerUi(app.Environment, app.Configuration))
    {
        // Serve the stub-user picker JS that augments the Swagger UI
        // topbar with a dev-only dropdown. Anonymous: the JS contains no
        // secrets, only the dev-fixture stub user list. RA-124.
        app.MapGet(SwaggerUiStubUserAssets.ScriptPath, () =>
            Results.Content(SwaggerUiStubUserAssets.ScriptBody, "application/javascript"))
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/openapi/v1.json", "EPR Management BE v1");
            options.RoutePrefix = "swagger";
            // Inject the stub-user picker into the topbar. The script
            // also monkey-patches window.fetch so every Try it out call
            // carries the four CDP trust headers for the selected user
            // — Swashbuckle's UseRequestInterceptor is unreliable with
            // arrow-function bodies, so we bypass it.
            options.InjectJavascript(SwaggerUiStubUserAssets.ScriptPath);
        });
    }

    app.MapWorkItemFrameworkEndpoints();
    // RA-131: framework-level SLA extend / override routes; mounted
    // outside MapWorkItemFrameworkEndpoints so the SLA surface can grow
    // (extend / override today, started / stopped / projection later)
    // without bloating the core endpoint group.
    app.MapWorkItemSlaEndpoints();
    app.MapWorkItemModules();
}
