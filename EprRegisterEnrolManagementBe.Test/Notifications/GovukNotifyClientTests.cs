using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.Utils.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notify.Interfaces;
using Notify.Models.Responses;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Polly;
using Polly.Retry;

namespace EprRegisterEnrolManagementBe.Test.Notifications;

public class GovukNotifyClientTests
{
    private static NotifyConfig ConfigWithTemplates(params (string key, string id)[] templates)
    {
        var cfg = new NotifyConfig();
        foreach (var (key, id) in templates)
        {
            cfg.Templates[key] = id;
        }
        return cfg;
    }

    private static GovukNotifyClient BuildSut(
        IAsyncNotificationClient inner,
        NotifyConfig? config = null,
        IStructuredLogger<GovukNotifyClient>? log = null
    )
    {
        config ??= ConfigWithTemplates(("DulyMade", "template-guid-1"));
        return new GovukNotifyClient(
            inner,
            Options.Create(config),
            log ?? Substitute.For<IStructuredLogger<GovukNotifyClient>>()
        );
    }

    /// <summary>
    /// Zero-delay retry pipeline with the same attempt count as the
    /// production pipeline so retry logic is exercised in tests without
    /// real exponential-backoff waits.
    /// </summary>
    private static ResiliencePipeline ZeroDelayRetryPipeline() =>
        new ResiliencePipelineBuilder()
            .AddRetry(
                new RetryStrategyOptions
                {
                    MaxRetryAttempts = 2,
                    Delay = TimeSpan.Zero,
                    BackoffType = DelayBackoffType.Constant,
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(),
                }
            )
            .Build();

    [Fact]
    public async Task SendEmailAsync_returns_success_with_provider_message_id_on_happy_path()
    {
        var inner = Substitute.For<IAsyncNotificationClient>();
        inner
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, dynamic>>(),
                Arg.Any<string>()
            )
            .Returns(new EmailNotificationResponse { id = "notify-msg-id-1" });

        var sut = BuildSut(inner);

        var result = await sut.SendEmailAsync(
            templateKey: "DulyMade",
            toEmail: "operator@example.com",
            personalisation: new Dictionary<string, string> { ["organisation_name"] = "Acme" },
            reference: "ref-1",
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.IsSuccess);
        Assert.Equal("notify-msg-id-1", result.ProviderMessageId);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task SendEmailAsync_passes_template_id_resolved_from_config_to_sdk()
    {
        var inner = Substitute.For<IAsyncNotificationClient>();
        inner
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, dynamic>>(),
                Arg.Any<string>()
            )
            .Returns(new EmailNotificationResponse { id = "x" });

        var config = ConfigWithTemplates(("SubmissionConfirmation", "template-guid-abc"));
        var sut = BuildSut(inner, config);

        await sut.SendEmailAsync(
            "SubmissionConfirmation",
            "op@ex.com",
            new Dictionary<string, string>(),
            "ref",
            cancellationToken: TestContext.Current.CancellationToken
        );

        await inner
            .Received(1)
            .SendEmailAsync(
                "op@ex.com",
                "template-guid-abc",
                Arg.Any<Dictionary<string, dynamic>>(),
                "ref"
            );
    }

    // ─────── RA-211: per-region reply-to resolution ───────

    [Fact]
    public async Task SendEmailAsync_passes_resolved_reply_to_id_for_a_configured_region()
    {
        var inner = Substitute.For<IAsyncNotificationClient>();
        inner
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, dynamic>>(),
                Arg.Any<string>(),
                Arg.Any<string>()
            )
            .Returns(new EmailNotificationResponse { id = "x" });

        var config = ConfigWithTemplates(("DulyMade", "t-id"));
        config.RegionToReplyToId["England"] = "reply-to-england";
        config.DefaultReplyToId = "reply-to-default";
        var sut = BuildSut(inner, config);

        await sut.SendEmailAsync(
            "DulyMade",
            "op@ex.com",
            new Dictionary<string, string>(),
            "ref",
            region: "England",
            cancellationToken: TestContext.Current.CancellationToken
        );

        // The region-specific mailbox wins over the default when both are
        // configured — the whole point of RegionToReplyToId existing at all.
        await inner
            .Received(1)
            .SendEmailAsync(
                "op@ex.com",
                "t-id",
                Arg.Any<Dictionary<string, dynamic>>(),
                "ref",
                "reply-to-england"
            );
    }

    [Fact]
    public async Task SendEmailAsync_falls_back_to_default_reply_to_id_for_an_unrecognised_region()
    {
        var inner = Substitute.For<IAsyncNotificationClient>();
        inner
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, dynamic>>(),
                Arg.Any<string>(),
                Arg.Any<string>()
            )
            .Returns(new EmailNotificationResponse { id = "x" });

        var config = ConfigWithTemplates(("DulyMade", "t-id"));
        config.RegionToReplyToId["England"] = "reply-to-england";
        config.DefaultReplyToId = "reply-to-default";
        var sut = BuildSut(inner, config);

        // "Wales" has no entry in RegionToReplyToId — must fall back to
        // DefaultReplyToId rather than sending with no reply-to at all.
        await sut.SendEmailAsync(
            "DulyMade",
            "op@ex.com",
            new Dictionary<string, string>(),
            "ref",
            region: "Wales",
            cancellationToken: TestContext.Current.CancellationToken
        );

        await inner
            .Received(1)
            .SendEmailAsync(
                "op@ex.com",
                "t-id",
                Arg.Any<Dictionary<string, dynamic>>(),
                "ref",
                "reply-to-default"
            );
    }

    [Fact]
    public async Task SendEmailAsync_passes_null_reply_to_id_when_region_and_default_are_both_unconfigured()
    {
        var inner = Substitute.For<IAsyncNotificationClient>();
        inner
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, dynamic>>(),
                Arg.Any<string>()
            )
            .Returns(new EmailNotificationResponse { id = "x" });

        var config = ConfigWithTemplates(("DulyMade", "t-id"));
        var sut = BuildSut(inner, config);

        // No region passed, nothing configured: falls back all the way to
        // "no reply-to override" (the Notify template's own configured
        // sender identity), not an exception or a bogus value.
        await sut.SendEmailAsync(
            "DulyMade",
            "op@ex.com",
            new Dictionary<string, string>(),
            "ref",
            cancellationToken: TestContext.Current.CancellationToken
        );

        await inner
            .Received(1)
            .SendEmailAsync("op@ex.com", "t-id", Arg.Any<Dictionary<string, dynamic>>(), "ref");
    }

    [Fact]
    public async Task SendEmailAsync_returns_failure_when_template_key_not_in_config()
    {
        var inner = Substitute.For<IAsyncNotificationClient>();
        var sut = BuildSut(inner, new NotifyConfig()); // empty templates

        var result = await sut.SendEmailAsync(
            templateKey: "MissingKey",
            toEmail: "op@ex.com",
            personalisation: new Dictionary<string, string>(),
            reference: "ref",
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(result.IsSuccess);
        Assert.Contains("MissingKey", result.ErrorMessage);
        await inner.DidNotReceiveWithAnyArgs().SendEmailAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task SendEmailAsync_retries_on_transient_failure_then_returns_success()
    {
        var inner = Substitute.For<IAsyncNotificationClient>();
        var callCount = 0;
        inner
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, dynamic>>(),
                Arg.Any<string>()
            )
            .Returns(_ =>
            {
                callCount++;
                if (callCount < 3)
                {
                    throw new Exception("transient");
                }

                return new EmailNotificationResponse { id = "retry-success-id" };
            });

        var config = ConfigWithTemplates(("DulyMade", "t-id"));
        // Zero-delay pipeline so the test doesn't incur real backoff waits.
        var sut = new GovukNotifyClient(
            inner,
            Options.Create(config),
            Substitute.For<IStructuredLogger<GovukNotifyClient>>(),
            retryPipeline: ZeroDelayRetryPipeline()
        );

        var result = await sut.SendEmailAsync(
            "DulyMade",
            "op@ex.com",
            new Dictionary<string, string>(),
            "ref",
            cancellationToken: TestContext.Current.CancellationToken
        );

        // ResiliencePipeline with zero delay retries just like production
        // but without waiting — verifies the 3-attempt call-count.
        Assert.True(result.IsSuccess);
        Assert.Equal("retry-success-id", result.ProviderMessageId);
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task SendEmailAsync_returns_failure_after_exhausting_retries()
    {
        var inner = Substitute.For<IAsyncNotificationClient>();
        inner
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, dynamic>>(),
                Arg.Any<string>()
            )
            .ThrowsAsync(new Exception("persistent failure"));

        var config = ConfigWithTemplates(("DulyMade", "t-id"));
        // Zero-delay pipeline so the test doesn't incur real backoff waits.
        var sut = new GovukNotifyClient(
            inner,
            Options.Create(config),
            Substitute.For<IStructuredLogger<GovukNotifyClient>>(),
            retryPipeline: ZeroDelayRetryPipeline()
        );

        var result = await sut.SendEmailAsync(
            "DulyMade",
            "op@ex.com",
            new Dictionary<string, string>(),
            "ref",
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(result.IsSuccess);
        Assert.Contains("persistent failure", result.ErrorMessage);
        // 3 total attempts (1 initial + 2 retries) with zero delay.
        await inner
            .Received(3)
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, dynamic>>(),
                Arg.Any<string>()
            );
    }

    [Fact]
    public async Task SendEmailAsync_emits_ecs_failure_log_when_retries_exhausted()
    {
        var inner = Substitute.For<IAsyncNotificationClient>();
        var boom = new Exception("persistent failure");
        inner
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, dynamic>>(),
                Arg.Any<string>()
            )
            .ThrowsAsync(boom);

        var log = Substitute.For<IStructuredLogger<GovukNotifyClient>>();
        var sut = new GovukNotifyClient(
            inner,
            Options.Create(ConfigWithTemplates(("DulyMade", "t-id"))),
            log,
            retryPipeline: ZeroDelayRetryPipeline()
        );

        await sut.SendEmailAsync(
            "DulyMade",
            "op@ex.com",
            new Dictionary<string, string>(),
            "ref-x",
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Terminal error carries the ECS event.* + error.* shape
        // OpenSearch queries depend on. Asserted via the injected
        // IStructuredLogger mock — no need to reach into ILogger
        // scope state.
        log.Received(1)
            .Log(
                LogLevel.Error,
                Arg.Any<string>(),
                Arg.Is<IReadOnlyDictionary<string, object?>>(p =>
                    (string)p["event.category"]! == "notify"
                    && (string)p["event.action"]! == "send_email"
                    && (string)p["event.outcome"]! == "failure"
                    && (string)p["event.reference"]! == "ref-x"
                    && (string)p["event.reason"]! == "send_failed_after_retries"
                ),
                boom
            );

        // NB: the test substitutes a zero-delay ResiliencePipeline that
        // omits the OnRetry hook, so retry-attempt warnings are exercised
        // separately via the production BuildRetryPipeline path.
    }

    [Fact]
    public async Task SendEmailAsync_failure_log_includes_sorted_personalisation_keys()
    {
        var inner = Substitute.For<IAsyncNotificationClient>();
        inner
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, dynamic>>(),
                Arg.Any<string>()
            )
            .ThrowsAsync(new Exception("Missing personalisation: sla_deadline"));

        var log = Substitute.For<IStructuredLogger<GovukNotifyClient>>();
        var sut = new GovukNotifyClient(
            inner,
            Options.Create(ConfigWithTemplates(("SlaExtended", "t-id"))),
            log,
            retryPipeline: ZeroDelayRetryPipeline()
        );

        // Deliberately unsorted insertion order — the log must emit them sorted.
        var personalisation = new Dictionary<string, string>
        {
            ["registration_number"] = "EX-001",
            ["organisation_name"] = "Acme",
            ["reference"] = "ref",
        };

        await sut.SendEmailAsync(
            "SlaExtended",
            "op@ex.com",
            personalisation,
            "ref-keys",
            cancellationToken: TestContext.Current.CancellationToken
        );

        // RA-201: sorted, comma-joined KEY NAMES (values never logged).
        log.Received(1)
            .Log(
                LogLevel.Error,
                Arg.Any<string>(),
                Arg.Is<IReadOnlyDictionary<string, object?>>(p =>
                    (string)p["event.reason"]! == "send_failed_after_retries"
                    && (string)p["notify.personalisation_keys"]!
                        == "organisation_name,reference,registration_number"
                ),
                Arg.Any<Exception>()
            );
    }

    [Fact]
    public async Task SendEmailAsync_entry_log_includes_sorted_personalisation_keys()
    {
        var inner = Substitute.For<IAsyncNotificationClient>();
        inner
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, dynamic>>(),
                Arg.Any<string>()
            )
            .Returns(new EmailNotificationResponse { id = "ok" });

        var log = Substitute.For<IStructuredLogger<GovukNotifyClient>>();
        var sut = new GovukNotifyClient(
            inner,
            Options.Create(ConfigWithTemplates(("SlaExtended", "t-id"))),
            log
        );

        var personalisation = new Dictionary<string, string>
        {
            ["registration_number"] = "EX-001",
            ["organisation_name"] = "Acme",
            ["sla_deadline"] = "1 January 2026",
            ["reference"] = "ref",
        };

        await sut.SendEmailAsync(
            "SlaExtended",
            "op@ex.com",
            personalisation,
            "ref-entry",
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Entry log ("Notify send starting") carries the keys for diagnosis.
        log.Received(1)
            .Log(
                LogLevel.Information,
                "Notify send starting",
                Arg.Is<IReadOnlyDictionary<string, object?>>(p =>
                    (string)p["notify.personalisation_keys"]!
                    == "organisation_name,reference,registration_number,sla_deadline"
                ),
                null
            );
    }

    [Fact]
    public async Task SendEmailAsync_emits_ecs_failure_log_when_template_missing()
    {
        var inner = Substitute.For<IAsyncNotificationClient>();
        var log = Substitute.For<IStructuredLogger<GovukNotifyClient>>();
        var sut = new GovukNotifyClient(inner, Options.Create(new NotifyConfig()), log);

        await sut.SendEmailAsync(
            "MissingKey",
            "op@ex.com",
            new Dictionary<string, string>(),
            "ref-m",
            cancellationToken: TestContext.Current.CancellationToken
        );

        log.Received(1)
            .Log(
                LogLevel.Error,
                Arg.Any<string>(),
                Arg.Is<IReadOnlyDictionary<string, object?>>(p =>
                    (string)p["event.category"]! == "notify"
                    && (string)p["event.action"]! == "send_email"
                    && (string)p["event.outcome"]! == "failure"
                    && (string)p["event.reason"]! == "template_not_configured"
                    && (string)p["notify.template_key"]! == "MissingKey"
                ),
                null
            );
    }

    [Fact]
    public async Task SendEmailAsync_emits_ecs_success_log_on_happy_path()
    {
        var inner = Substitute.For<IAsyncNotificationClient>();
        inner
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, dynamic>>(),
                Arg.Any<string>()
            )
            .Returns(new EmailNotificationResponse { id = "ok" });

        var log = Substitute.For<IStructuredLogger<GovukNotifyClient>>();
        var sut = new GovukNotifyClient(
            inner,
            Options.Create(ConfigWithTemplates(("DulyMade", "t-id"))),
            log
        );

        await sut.SendEmailAsync(
            "DulyMade",
            "op@ex.com",
            new Dictionary<string, string>(),
            "ref-ok",
            cancellationToken: TestContext.Current.CancellationToken
        );

        log.Received(1)
            .Log(
                LogLevel.Information,
                Arg.Any<string>(),
                Arg.Is<IReadOnlyDictionary<string, object?>>(p =>
                    (string)p["event.category"]! == "notify"
                    && (string)p["event.action"]! == "send_email"
                    && (string)p["event.outcome"]! == "success"
                    && (string)p["event.reference"]! == "ref-ok"
                ),
                null
            );
    }
}
