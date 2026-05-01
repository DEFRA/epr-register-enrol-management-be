using Elastic.Serilog.Enrichers.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace EprRegisterEnrolManagementBe.Utils.Logging;

public static class CdpLogging
{
    /// <summary>
    /// Serilog configuration callback used by the
    /// <c>UseSerilog((ctx, services, config) =&gt; ...)</c> overload in
    /// <c>Program.cs</c>. The <see cref="IServiceProvider"/> overload is
    /// required so the <see cref="IHttpContextAccessor"/> can be
    /// resolved from DI: <c>IConfiguration.Get&lt;HttpContextAccessor&gt;()</c>
    /// merely binds configuration onto a brand-new instance whose
    /// <see cref="IHttpContextAccessor.HttpContext"/> is always
    /// <c>null</c>, so the ECS HTTP enricher silently emits no
    /// request-context fields (epr-3yv).
    /// </summary>
    public static void Configuration(HostBuilderContext ctx, IServiceProvider services, LoggerConfiguration config)
    {
        var httpAccessor = services.GetRequiredService<IHttpContextAccessor>();
        var traceIdHeader = ctx.Configuration.GetValue<string>("TraceHeader");

        // epr-mhi: the deprecated AuditLogger sub-pipeline (and its
        // ExcludeAuditEvents filter on the main logger) was removed per
        // ADR-0004 — the canonical audit record is the on-document
        // WorkItem.AuditLog, not a side-channel Serilog stream. Operational
        // logs flow through the single configured logger.
        var mainLogger = new LoggerConfiguration()
            .ReadFrom.Configuration(ctx.Configuration)
            .Enrich.WithEcsHttpContext(httpAccessor)
            .Enrich.FromLogContext()
            .CreateLogger();

        if (traceIdHeader != null)
        {
            config.Enrich.WithCorrelationId(traceIdHeader);
        }

        config.WriteTo.Logger(mainLogger);
    }
}