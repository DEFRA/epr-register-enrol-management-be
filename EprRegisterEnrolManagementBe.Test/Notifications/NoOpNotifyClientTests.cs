using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.Utils.Logging;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.Notifications;

public class NoOpNotifyClientTests
{
    [Fact]
    public async Task SendEmailAsync_returns_success_with_no_provider_message_id()
    {
        var sut = new NoOpNotifyClient(Substitute.For<IStructuredLogger<NoOpNotifyClient>>());

        var result = await sut.SendEmailAsync(
            templateKey: "DulyMade",
            toEmail: "operator@example.com",
            personalisation: new Dictionary<string, string> { ["organisation_name"] = "Acme" },
            reference: "ref-1",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(result.ProviderMessageId);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task SendEmailAsync_emits_ecs_success_log_matching_real_client_shape()
    {
        var log = Substitute.For<IStructuredLogger<NoOpNotifyClient>>();
        var sut = new NoOpNotifyClient(log);

        await sut.SendEmailAsync(
            "DulyMade", "op@ex.com",
            new Dictionary<string, string>(), "ref-noop",
            TestContext.Current.CancellationToken);

        // Same category/action as GovukNotifyClient so dashboards can
        // treat dev / no-op traffic the same as real traffic.
        log.Received(1).Log(
            LogLevel.Information,
            Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<string, object?>>(p =>
                (string)p["event.category"]! == "notify"
                && (string)p["event.action"]! == "send_email"
                && (string)p["event.outcome"]! == "success"
                && (string)p["event.reference"]! == "ref-noop"
                && (string)p["notify.template_key"]! == "DulyMade"),
            null);
    }
}

