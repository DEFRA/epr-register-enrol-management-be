using System.Diagnostics.CodeAnalysis;
using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Health;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.Utils;
using EprRegisterEnrolManagementBe.Utils.Background;
using EprRegisterEnrolManagementBe.Utils.Http;
using EprRegisterEnrolManagementBe.Utils.Logging;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx.Http;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MongoDB.Driver.Authentication.AWS;
using Notify.Client;
using Notify.Interfaces;
using Serilog;

var app = BuildApp(args);
await RunStartupMigrations(app);
await app.RunAsync();

// One-shot, best-effort data migrations that run BEFORE the host starts (and
// therefore before WorkItemPersistence builds its indexes). Register migrations
// in the array below — each runs once per boot, should be idempotent, and must
// be removed once it has run in every environment. The harness is kept in place
// even when empty so the next correction is a one-line change. See the
// "Startup migrations" section of the README.
[ExcludeFromCodeCoverage]
static Task RunStartupMigrations(WebApplication app) =>
    StartupMigrationRunner.RunAsync(
        app.Services,
        app.Logger,
        migrations:
        [
            // epr-2uxy: READ-ONLY diagnostic — writes nothing, and is a no-op
            // unless Diagnostics:Ra292IsNewSiteAudit is set. Registered on this
            // harness because CDP offers no way to run ad-hoc mongosh against a
            // deployed database, and the open question is precisely whether a
            // deployed environment retained affected data.
            ("epr-2uxy-isnewsite-audit (read-only)",
                ReAccreditationIsNewSiteAudit.RunAsync),
        ]);

[ExcludeFromCodeCoverage]
static WebApplication BuildApp(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);

    ConfigureHost(builder);
    ConfigureServices(builder);

    var app = builder.Build();

    LogNotifyClientRegistration(app);

    ConfigureMiddleware(app);
    ConfigureEndpoints(app);

    return app;
}

[ExcludeFromCodeCoverage]
static void ConfigureHost(WebApplicationBuilder builder)
{
    builder.Host.UseSerilog(CdpLogging.Configuration);

    // Suppress the "Server: Kestrel" response header. It's a minor
    // fingerprinting aid with no direct exploit path, but there's no
    // reason to advertise the server stack (pentest 2026-08-08, L4).
    builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
}

[ExcludeFromCodeCoverage]
static void ConfigureServices(WebApplicationBuilder builder)
{
    var services = builder.Services;
    var configuration = builder.Configuration;

    // Trust material must be loaded before anything creates outbound connections.
    services.LoadCustomTrustStoreFromEnvironment();

    // HttpClient.DefaultProxy MUST also be set before any bare HttpClient
    // is constructed. The GovukNotify SDK does `new HttpClient()` and
    // therefore depends on DefaultProxy — in CDP the runtime's
    // automatic env-var detection does not reliably fire, so we wire
    // it from HTTP(S)_PROXY explicitly to keep outbound Notify traffic
    // on the Squid proxy. Without this the SDK egresses direct and
    // hangs on the platform's deny-all firewall
    // (cdp-documentation/how-to/proxy.md).
    //
    // This is process-global: it silently applies to every HttpClient in
    // the process that doesn't explicitly opt out, not just Notify's bare
    // one — including named clients from AddHttpClient(...) that don't
    // configure a primary handler (an omitted handler is NOT the same as
    // "unproxied"; it just inherits this). Any client that must bypass
    // Squid (e.g. "DefaultClient" below, for CDP-service-to-CDP-service
    // calls) has to say so explicitly via
    // ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    // { UseProxy = false }) — see RA-311 query-push-proxy-fix.
    var defaultProxy = ProxyHttpMessageHandler.BuildDefaultProxy();
    if (defaultProxy is not null)
    {
        HttpClient.DefaultProxy = defaultProxy;
    }

    services.AddExceptionHandler<ExceptionLoggingHandler>();
    services.AddProblemDetails();
    services.AddValidation();
    services.AddSingleton(TimeProvider.System);

    // Generic structured-logging facade: caller-defined property bag,
    // routed through ILogger<T> so source-context is preserved. Open
    // generic so consumers depend on IStructuredLogger<TTheirComponent>.
    services.AddSingleton(typeof(IStructuredLogger<>), typeof(StructuredLogger<>));

    // Built-in .NET 10 OpenAPI document generation. Document only — no
    // Swagger UI is wired up (that ships in a separate package). The
    // document is exposed unauthenticated at /openapi/v1.json by
    // ConfigureEndpoints, mirroring the health endpoints' posture, since
    // BFF and platform tooling need to fetch it without credentials.
    services.AddOpenApi(
        "v1",
        options =>
        {
            // Inject concrete request-body examples so the Swagger UI
            // "Try it out" panel pre-fills with real payloads. Runs last in
            // the transformer pipeline to survive schema population. RA-124.
            // AddDocumentTransformer<T> registers the transformer in DI, so
            // no separate AddSingleton call is required.
            options.AddDocumentTransformer<WorkItemOpenApiExampleTransformer>();
        }
    );

    services.AddHttpContextAccessor();
    // In-memory cache backs the HMAC nonce replay defence in
    // ClientIdAuthenticationHandler. Singleton by default.
    services.AddMemoryCache();

    ConfigureAuth(services, configuration);

    ConfigureHeaderPropagation(services, configuration);
    ConfigureHttpClients(services);
    ConfigureMongo(services, configuration);
    ConfigureCors(services, configuration);
    ConfigureNotifications(services, configuration);
    ConfigureReEx(services, configuration);
    ConfigureOperatorBackendPush(services, configuration);

    services
        .AddOptions<LivenessHealthCheckOptions>()
        .Bind(configuration.GetSection("Liveness"))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    services
        .AddHealthChecks()
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
    // (universal rules: max-extension cap, audit shape, operator notify on
    // extend via post-action hooks) so it lives next to the framework, not
    // in a module.
    services.AddOptions<SlaConfig>().Bind(builder.Configuration.GetSection("WorkItems:Sla"));
    // RA-133: accreditation issuance config (current year drives the
    // generated AccreditationId year segment and AccreditationStartDate).
    services
        .AddOptions<AccreditationConfig>()
        .Bind(builder.Configuration.GetSection("Accreditation"));
    // RA-291 (AC06): public operator-service base URL, surfaced in the
    // Queried Notify email. Read from the flat OPERATOR_SERVICE_BASE_URL
    // environment variable, matching NOTIFY_API_KEY's convention.
    services
        .AddOptions<OperatorServiceConfig>()
        .Configure<IConfiguration>(
            (options, configuration) =>
                options.BaseUrl =
                    configuration.GetValue<string>("OPERATOR_SERVICE_BASE_URL") ?? string.Empty
        );
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
    services.AddAuthentication(ClientIdDefaults.AuthenticationScheme).AddClientId();

    // Bind options lazily via PostConfigure so test fixtures that add
    // config via WebApplicationFactory.ConfigureAppConfiguration (which
    // fires during builder.Build(), after this method runs) can still
    // override the value.
    services
        .AddOptions<ClientIdAuthenticationOptions>(ClientIdDefaults.AuthenticationScheme)
        .Configure<IConfiguration>(
            (options, config) =>
            {
                options.ClientSecrets = BuildClientSecrets(config);
                options.MaxClientIdLength = config.GetValue(
                    "Auth:MaxClientIdLength",
                    options.MaxClientIdLength
                );
                options.MaxUserIdLength = config.GetValue(
                    "Auth:MaxUserIdLength",
                    options.MaxUserIdLength
                );
                options.MaxUserNameLength = config.GetValue(
                    "Auth:MaxUserNameLength",
                    options.MaxUserNameLength
                );
                options.MaxSignatureLength = config.GetValue(
                    "Auth:MaxSignatureLength",
                    options.MaxSignatureLength
                );
                options.MaxTimestampLength = config.GetValue(
                    "Auth:MaxTimestampLength",
                    options.MaxTimestampLength
                );
                options.MaxNonceLength = config.GetValue(
                    "Auth:MaxNonceLength",
                    options.MaxNonceLength
                );
            }
        );

    services.AddAuthorization();
}

// RA-345: per-caller secrets, keyed by the clientId each caller is expected
// to assert. Each caller gets its own secret env var so one can be rotated
// without touching the other — see ADR-0005's "Follow-up: per-caller
// shared secrets" section and CaseManagementAuthConfig in
// epr-register-enrol-backend for the single-caller precedent this mirrors.
[ExcludeFromCodeCoverage]
static IReadOnlyDictionary<string, string> BuildClientSecrets(IConfiguration config)
{
    var map = new Dictionary<string, string>(StringComparer.Ordinal);
    // Config-key form, NOT the literal env var name: EnvironmentVariablesConfigurationProvider
    // rewrites "__" to ":" while loading, so the real env var
    // AUTH_SHARED_SECRET__MANAGEMENT_FE is stored under config key
    // "AUTH_SHARED_SECRET:MANAGEMENT_FE" — a GetValue call using the literal
    // double-underscore string never matches it. Same pattern OperatorBackendApi
    // uses elsewhere in this file (GetSection("OperatorBackendApi"), not a
    // literal "OperatorBackendApi__Url" GetValue key).
    AddCallerSecret(
        map,
        config,
        callerName: "ManagementFe",
        clientIdKey: "Auth:ManagementFeClientId",
        clientIdDefault: "frontend",
        secretKey: "AUTH_SHARED_SECRET:MANAGEMENT_FE"
    );
    AddCallerSecret(
        map,
        config,
        callerName: "Backend",
        clientIdKey: "Auth:BackendClientId",
        clientIdDefault: "epr-register-enrol-backend",
        secretKey: "AUTH_SHARED_SECRET:BACKEND"
    );
    return map;
}

[ExcludeFromCodeCoverage]
static void AddCallerSecret(
    Dictionary<string, string> map,
    IConfiguration config,
    string callerName,
    string clientIdKey,
    string clientIdDefault,
    string secretKey
)
{
    var secret = config.GetValue<string>(secretKey);
    if (string.IsNullOrEmpty(secret))
    {
        // Caller not configured yet — same "not signed" posture as today
        // when AUTH_SHARED_SECRET was unset.
        return;
    }

    var clientId = config.GetValue(clientIdKey, clientIdDefault);

    // Fail LOUD: two callers configured to assert the same clientId would
    // mean the second caller's secret silently overwrites the first's in
    // the map, breaking the first caller's signing and defeating the
    // entire point of per-caller secrets (RA-345). Always a config
    // mistake — never a legitimate state — so this throws rather than
    // silently letting the later AddCallerSecret call win.
    if (map.ContainsKey(clientId))
    {
        throw new InvalidOperationException(
            $"ClientIdAuthentication misconfigured: caller '{callerName}' asserts "
                + $"clientId '{clientId}' (via {clientIdKey}), which is already registered to "
                + "another caller. Each caller must have a distinct clientId — check for a "
                + "copy-pasted or missing override."
        );
    }

    map[clientId] = secret;
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
        //   * x-cdp-user-id, x-cdp-user-name,
        //     x-cdp-client-id — identity headers from the BFF are
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

    // RA-311/MBE-1: plain, unproxied client for direct CDP-service-to-CDP-service
    // calls (the operator backend push adapter) — mirrors the operator
    // backend's own "DefaultClient" registration, which it uses for its own
    // calls into this service. UseProxy = false is required, not optional:
    // omitting the primary handler here would silently inherit
    // HttpClient.DefaultProxy (set process-wide above for the Notify SDK),
    // routing this internal cdp-int.defra.cloud traffic through Squid, which
    // doesn't allow-list it and 502s the tunnel (see RA-311
    // query-push-proxy-fix; caught this in CDP `test`, not locally, because
    // HTTPS_PROXY is only set in CDP).
    services
        .AddHttpClient("DefaultClient")
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseProxy = false })
        .AddHeaderPropagation();

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

/// <summary>
/// ReEx accreditation client wiring. When <c>REEX_API_BASIC_AUTH_USERNAME</c>
/// is absent or empty the stub client is registered so the service still boots
/// in local and CI environments without ReEx credentials. When set, the real
/// HTTP client is registered with Basic Auth behind
/// <see cref="IReExAccreditationClient"/> and routed through the corporate proxy.
/// The Base URL is read from the <c>ReExApi:BaseUrl</c> config section (same
/// section name as the operator backend for consistency).
/// </summary>
[ExcludeFromCodeCoverage]
static void ConfigureReEx(IServiceCollection services, IConfiguration configuration)
{
    services.AddOptions<ReExAccreditationConfig>().Bind(configuration.GetSection("ReExApi"));

    var username = configuration.GetValue<string>("REEX_API_BASIC_AUTH_USERNAME");

    if (string.IsNullOrWhiteSpace(username))
    {
        services.AddSingleton<IReExAccreditationClient, StubReExAccreditationClient>();
        return;
    }

    services.Configure<ReExAccreditationCredentials>(creds =>
    {
        creds.Username = username;
        creds.Password =
            configuration.GetValue<string>("REEX_API_BASIC_AUTH_PASSWORD") ?? string.Empty;
    });
    services.AddTransient<ReExBasicAuthHandler>();
    services
        .AddHttpClientWithProxy<IReExAccreditationClient, HttpReExAccreditationClient>()
        .AddHttpMessageHandler<ReExBasicAuthHandler>();
}

/// <summary>
/// RA-311/MBE-1: outbound push-to-operator-backend wiring. Adapter selection
/// is keyed off the explicit <c>OperatorBackendApi:Enabled</c> switch
/// (defaulting to <c>false</c>), not off whether <c>Url</c> happens to be
/// set — so a deploy of this code is behaviour-neutral until the flag is
/// turned on per environment, and the same flag doubles as the rollback
/// lever (MBE-F5). When disabled, the no-op adapter is registered (the
/// query flow still succeeds — the push is fire-and-forget) and a startup
/// warning is logged once via
/// <see cref="OperatorBackendPushDisabledWarningHostedService"/>. When
/// enabled, <see cref="OperatorBackendApiConfigValidator"/> fails startup if
/// <c>Url</c>/<c>ClientId</c>/<c>SharedSecret</c> are incomplete, rather than
/// letting every push fail silently at runtime. Mirrors
/// <see cref="ConfigureReEx"/>'s stub/real selection.
/// </summary>
[ExcludeFromCodeCoverage]
static void ConfigureOperatorBackendPush(IServiceCollection services, IConfiguration configuration)
{
    // SharedSecret is deliberately NOT part of the OperatorBackendApi__* section —
    // CDP's secrets naming convention is a flat UPPER_SNAKE_CASE name, not the
    // nested Section__Property form the rest of this config uses — so it's
    // sourced from OPERATOR_BACKEND_SHARED_SECRET instead (same flat name the
    // operator backend's CaseManagementAuth verifies inbound pushes against).
    services
        .AddOptions<OperatorBackendApiConfig>()
        .Configure<IConfiguration>(
            (options, config) =>
            {
                config.GetSection("OperatorBackendApi").Bind(options);
                options.SharedSecret = config.GetValue<string>("OPERATOR_BACKEND_SHARED_SECRET");
            }
        )
        .ValidateOnStart();
    services.AddSingleton<
        IValidateOptions<OperatorBackendApiConfig>,
        OperatorBackendApiConfigValidator
    >();

    var enabled = configuration.GetSection("OperatorBackendApi").GetValue("Enabled", false);

    if (!enabled)
    {
        services.AddSingleton<IOperatorBackendPushAdapter, NullOperatorBackendPushAdapter>();
        services.AddHostedService<OperatorBackendPushDisabledWarningHostedService>();
        return;
    }

    services.AddSingleton<IOperatorBackendPushAdapter, HttpOperatorBackendPushAdapter>();
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
    services.AddOptions<NotifyConfig>().Bind(configuration.GetSection("Notify"));

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
            : new NotificationClient(baseUri, apiKey)
    );
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
    services
        .AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>()
        .Configure<IConfiguration>(
            (options, config) =>
            {
                var allowedOrigins =
                    config.GetSection("Cors:AllowedOrigins").Get<string[]>()
                    ?? Array.Empty<string>();
                var traceHeader = config.GetValue<string>("TraceHeader");

                options.AddPolicy(
                    "BackendCors",
                    policy =>
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
                        //   * x-cdp-user-id, x-cdp-user-name,
                        //     x-cdp-client-id — identity headers are
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

                        policy
                            .WithOrigins(allowedOrigins)
                            .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
                            .WithHeaders([.. allowedHeaders])
                            .DisallowCredentials();
                    }
                );
            }
        );
}

/// <summary>
/// Emit a single startup log entry naming the registered
/// <see cref="INotifyClient"/> implementation and the active Notify
/// configuration. Surfaces silent misconfiguration (missing API key,
/// wrong base URI) in docker / CDP logs the moment the host starts.
/// </summary>
[ExcludeFromCodeCoverage]
static void LogNotifyClientRegistration(WebApplication app)
{
    var notifyClient = app.Services.GetRequiredService<INotifyClient>();
    var notifyOptions = app
        .Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotifyConfig>>()
        .Value;
    var apiKeyConfigured = !string.IsNullOrWhiteSpace(
        app.Configuration.GetValue<string>("NOTIFY_API_KEY")
    );
    app.Logger.LogInformation(
        "Notify integration: client={NotifyClientType} apiKeyConfigured={ApiKeyConfigured} "
            + "baseUri={NotifyBaseUri} timeoutSeconds={NotifyTimeoutSeconds} templates={NotifyTemplateCount}",
        notifyClient.GetType().FullName,
        apiKeyConfigured,
        string.IsNullOrWhiteSpace(notifyOptions.BaseUri) ? "<sdk-default>" : notifyOptions.BaseUri,
        notifyOptions.RequestTimeoutSeconds,
        notifyOptions.Templates.Count
    );
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
    app.MapHealthChecks(
            "/health",
            new HealthCheckOptions { Predicate = check => check.Tags.Contains("live") }
        )
        .AllowAnonymous()
        .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get, HttpMethods.Head }));
    // Readiness: only the "ready"-tagged checks run (currently MongoDB).
    // CDP / Kubernetes uses this to decide when to send traffic.
    app.MapHealthChecks(
            "/health/ready",
            new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }
        )
        .AllowAnonymous()
        .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get, HttpMethods.Head }));

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
        app.MapGet(
                SwaggerUiStubUserAssets.ScriptPath,
                () => Results.Content(SwaggerUiStubUserAssets.ScriptBody, "application/javascript")
            )
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
