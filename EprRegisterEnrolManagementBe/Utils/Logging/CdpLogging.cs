using System.Diagnostics.CodeAnalysis;
using Elastic.Serilog.Enrichers.Web;
using Serilog;

namespace EprRegisterEnrolManagementBe.Utils.Logging;

public static class CdpLogging
{
    [ExcludeFromCodeCoverage]
    public static void Configuration(HostBuilderContext ctx, LoggerConfiguration config)
    {
        var httpAccessor = ctx.Configuration.Get<HttpContextAccessor>();
        var traceIdHeader = ctx.Configuration.GetValue<string>("TraceHeader");

        // epr-mhi: the deprecated AuditLogger sub-pipeline (and its
        // ExcludeAuditEvents filter on the main logger) was removed per
        // ADR-0004 — the canonical audit record is the on-document
        // WorkItem.AuditLog, not a side-channel Serilog stream. Operational
        // logs flow through the single configured logger.
        var mainLogger = new LoggerConfiguration()
            .ReadFrom.Configuration(ctx.Configuration)
            .Enrich.WithEcsHttpContext(httpAccessor!)
            .Enrich.FromLogContext()
            .CreateLogger();

        if (traceIdHeader != null)
        {
            config.Enrich.WithCorrelationId(traceIdHeader);
        }

        config.WriteTo.Logger(mainLogger);
    }
}