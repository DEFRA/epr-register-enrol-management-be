using EprRegisterEnrolManagementBe.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
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
        NotifyConfig? config = null)
    {
        config ??= ConfigWithTemplates(("DulyMade", "template-guid-1"));
        return new GovukNotifyClient(
            inner,
            Options.Create(config),
            NullLogger<GovukNotifyClient>.Instance);
    }

    /// <summary>
    /// Zero-delay retry pipeline with the same attempt count as the
    /// production pipeline so retry logic is exercised in tests without
    /// real exponential-backoff waits.
    /// </summary>
    private static ResiliencePipeline ZeroDelayRetryPipeline() =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero,
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = new PredicateBuilder().Handle<Exception>()
            })
            .Build();

    [Fact]
    public async Task SendEmailAsync_returns_success_with_provider_message_id_on_happy_path()
    {
        var inner = Substitute.For<IAsyncNotificationClient>();
        inner.SendEmailAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, dynamic>>(),
                Arg.Any<string>())
            .Returns(new EmailNotificationResponse { id = "notify-msg-id-1" });

        var sut = BuildSut(inner);

        var result = await sut.SendEmailAsync(
            templateKey: "DulyMade",
            toEmail: "operator@example.com",
            personalisation: new Dictionary<string, string> { ["organisation_name"] = "Acme" },
            reference: "ref-1",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("notify-msg-id-1", result.ProviderMessageId);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task SendEmailAsync_passes_template_id_resolved_from_config_to_sdk()
    {
        var inner = Substitute.For<IAsyncNotificationClient>();
        inner.SendEmailAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, dynamic>>(),
                Arg.Any<string>())
            .Returns(new EmailNotificationResponse { id = "x" });

        var config = ConfigWithTemplates(("SubmissionConfirmation", "template-guid-abc"));
        var sut = BuildSut(inner, config);

        await sut.SendEmailAsync(
            "SubmissionConfirmation", "op@ex.com",
            new Dictionary<string, string>(), "ref",
            TestContext.Current.CancellationToken);

        await inner.Received(1).SendEmailAsync(
            "op@ex.com",
            "template-guid-abc",
            Arg.Any<Dictionary<string, dynamic>>(),
            "ref");
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains("MissingKey", result.ErrorMessage);
        await inner.DidNotReceiveWithAnyArgs().SendEmailAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task SendEmailAsync_retries_on_transient_failure_then_returns_success()
    {
        var inner = Substitute.For<IAsyncNotificationClient>();
        var callCount = 0;
        inner.SendEmailAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, dynamic>>(),
                Arg.Any<string>())
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
            NullLogger<GovukNotifyClient>.Instance,
            retryPipeline: ZeroDelayRetryPipeline());

        var result = await sut.SendEmailAsync(
            "DulyMade", "op@ex.com",
            new Dictionary<string, string>(), "ref",
            TestContext.Current.CancellationToken);

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
        inner.SendEmailAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, dynamic>>(),
                Arg.Any<string>())
            .ThrowsAsync(new Exception("persistent failure"));

        var config = ConfigWithTemplates(("DulyMade", "t-id"));
        // Zero-delay pipeline so the test doesn't incur real backoff waits.
        var sut = new GovukNotifyClient(
            inner,
            Options.Create(config),
            NullLogger<GovukNotifyClient>.Instance,
            retryPipeline: ZeroDelayRetryPipeline());

        var result = await sut.SendEmailAsync(
            "DulyMade", "op@ex.com",
            new Dictionary<string, string>(), "ref",
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains("persistent failure", result.ErrorMessage);
        // 3 total attempts (1 initial + 2 retries) with zero delay.
        await inner.Received(3).SendEmailAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Dictionary<string, dynamic>>(),
            Arg.Any<string>());
    }
}
