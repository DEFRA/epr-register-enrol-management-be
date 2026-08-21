using Elastic.CommonSchema.Serilog;
using Serilog.Core;
using Serilog.Events;

namespace EprRegisterEnrolManagementBe.Utils.Logging;

/// <summary>
/// Redacts PII from log events before they reach any sink. Must be registered
/// after <c>Enrich.WithEcsHttpContext(...)</c> so the client IP it captures
/// still exists on the log event when this enricher runs.
/// </summary>
public sealed class PiiRedactionEnricher : ILogEventEnricher
{
    public const string RedactedValue = "[REDACTED]";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        RedactHttpContextClientIp(logEvent);
        RedactTopLevelEmail(logEvent, propertyFactory);
    }

    private static void RedactHttpContextClientIp(LogEvent logEvent)
    {
        if (
            !logEvent.Properties.TryGetValue(
                SpecialProperties.SpecialKeys.HttpContext,
                out var value
            )
            || value
                is not ScalarValue { Value: SpecialProperties.HttpContextEnrichments enrichments }
            || enrichments.Client is null
        )
        {
            return;
        }

        // client.address is sometimes populated with the same raw IP as client.ip
        // (ECS spec: ".address" holds the raw value, duplicated to ".ip").
        enrichments.Client.Ip = RedactedValue;
        enrichments.Client.Address = RedactedValue;
    }

    private static void RedactTopLevelEmail(
        LogEvent logEvent,
        ILogEventPropertyFactory propertyFactory
    )
    {
        if (!logEvent.Properties.ContainsKey("Email"))
        {
            return;
        }

        logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty("Email", RedactedValue));
    }
}
