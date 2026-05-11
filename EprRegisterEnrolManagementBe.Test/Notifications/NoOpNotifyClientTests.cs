using EprRegisterEnrolManagementBe.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace EprRegisterEnrolManagementBe.Test.Notifications;

public class NoOpNotifyClientTests
{
    [Fact]
    public async Task SendEmailAsync_returns_success_with_no_provider_message_id()
    {
        var sut = new NoOpNotifyClient(NullLogger<NoOpNotifyClient>.Instance);

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
}
