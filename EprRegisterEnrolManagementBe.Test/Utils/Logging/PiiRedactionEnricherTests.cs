using Elastic.CommonSchema;
using Elastic.CommonSchema.Serilog;
using EprRegisterEnrolManagementBe.Utils.Logging;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace EprRegisterEnrolManagementBe.Test.Utils.Logging;

public class PiiRedactionEnricherTests
{
    private static readonly MessageTemplateParser TemplateParser = new();

    [Fact]
    public void Enrich_RedactsHttpContextClientIpAndAddress_WhenPresent()
    {
        var enrichments = new SpecialProperties.HttpContextEnrichments
        {
            Client = new Client { Ip = "203.0.113.5", Address = "203.0.113.5" },
        };
        var logEvent = CreateLogEvent(
            new LogEventProperty(
                SpecialProperties.SpecialKeys.HttpContext,
                new ScalarValue(enrichments)
            )
        );

        new PiiRedactionEnricher().Enrich(logEvent, new TestPropertyFactory());

        Assert.Equal(PiiRedactionEnricher.RedactedValue, enrichments.Client!.Ip);
        Assert.Equal(PiiRedactionEnricher.RedactedValue, enrichments.Client!.Address);
    }

    [Fact]
    public void Enrich_RedactsNameAndEmail_OnClientUserAndTopLevelUser()
    {
        var clientUser = new User
        {
            Id = "user-1",
            Name = "jdoe",
            FullName = "Jane Doe",
            Email = "jane.doe@example.com",
            Hash = "abc123",
        };
        var topLevelUser = new User
        {
            Id = "user-2",
            Name = "jsmith",
            Email = "john.smith@example.com",
        };
        var enrichments = new SpecialProperties.HttpContextEnrichments
        {
            Client = new Client { Ip = "203.0.113.5", User = clientUser },
            User = topLevelUser,
        };
        var logEvent = CreateLogEvent(
            new LogEventProperty(
                SpecialProperties.SpecialKeys.HttpContext,
                new ScalarValue(enrichments)
            )
        );

        new PiiRedactionEnricher().Enrich(logEvent, new TestPropertyFactory());

        Assert.Null(clientUser.Name);
        Assert.Null(clientUser.FullName);
        Assert.Null(clientUser.Email);
        Assert.Null(clientUser.Hash);
        Assert.Equal("user-1", clientUser.Id);

        Assert.Null(topLevelUser.Name);
        Assert.Null(topLevelUser.Email);
        Assert.Equal("user-2", topLevelUser.Id);
    }

    [Fact]
    public void Enrich_DoesNotThrow_WhenHttpContextPropertyIsAbsent()
    {
        var logEvent = CreateLogEvent();

        var exception = Record.Exception(() =>
            new PiiRedactionEnricher().Enrich(logEvent, new TestPropertyFactory())
        );

        Assert.Null(exception);
    }

    [Fact]
    public void Enrich_DoesNotThrow_WhenHttpContextHasNoClientOrUser()
    {
        var enrichments = new SpecialProperties.HttpContextEnrichments { Client = null };
        var logEvent = CreateLogEvent(
            new LogEventProperty(
                SpecialProperties.SpecialKeys.HttpContext,
                new ScalarValue(enrichments)
            )
        );

        var exception = Record.Exception(() =>
            new PiiRedactionEnricher().Enrich(logEvent, new TestPropertyFactory())
        );

        Assert.Null(exception);
    }

    [Fact]
    public void Enrich_RedactsTopLevelEmailProperty_WhenPresent()
    {
        var logEvent = CreateLogEvent(
            new LogEventProperty("Email", new ScalarValue("person@example.com"))
        );

        new PiiRedactionEnricher().Enrich(logEvent, new TestPropertyFactory());

        Assert.Equal(
            new ScalarValue(PiiRedactionEnricher.RedactedValue),
            logEvent.Properties["Email"]
        );
    }

    [Fact]
    public void Enrich_RedactsEmailEmbeddedInAnyStringProperty_RegardlessOfPropertyName()
    {
        // Mirrors HttpAccreditationNumberAdapter.cs / HttpOperatorBackendPushAdapter.cs's
        // `{Body}` logging, which can echo a submitted email back inside the
        // operator backend's raw error text.
        var logEvent = CreateLogEvent(
            new LogEventProperty(
                "Body",
                new ScalarValue("{\"error\":\"Invalid contact email: jane.doe@example.com\"}")
            )
        );

        new PiiRedactionEnricher().Enrich(logEvent, new TestPropertyFactory());

        var redacted = (ScalarValue)logEvent.Properties["Body"];
        Assert.Equal("{\"error\":\"Invalid contact email: [REDACTED]\"}", redacted.Value);
    }

    [Fact]
    public void Enrich_LeavesNonPiiPropertiesUntouched()
    {
        var logEvent = CreateLogEvent(
            new LogEventProperty("CorrelationId", new ScalarValue("abc-123")),
            new LogEventProperty("WorkItemId", new ScalarValue("wi-456")),
            new LogEventProperty("event.category", new ScalarValue("notify"))
        );

        new PiiRedactionEnricher().Enrich(logEvent, new TestPropertyFactory());

        Assert.Equal(new ScalarValue("abc-123"), logEvent.Properties["CorrelationId"]);
        Assert.Equal(new ScalarValue("wi-456"), logEvent.Properties["WorkItemId"]);
        Assert.Equal(new ScalarValue("notify"), logEvent.Properties["event.category"]);
    }

    private static LogEvent CreateLogEvent(params LogEventProperty[] properties) =>
        new(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            null,
            TemplateParser.Parse("Test message"),
            properties
        );

    private sealed class TestPropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(
            string name,
            object? value,
            bool destructureObjects = false
        ) => new(name, new ScalarValue(value));
    }
}
