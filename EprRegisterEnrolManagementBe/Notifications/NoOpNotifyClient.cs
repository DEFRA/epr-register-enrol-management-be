using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolManagementBe.Notifications;

/// <summary>
/// Fallback <see cref="INotifyClient"/> registered when
/// <see cref="NotifyConfig.ApiKey"/> is missing. Logs the intended send
/// at <see cref="LogLevel.Information"/> and returns success with a
/// null provider message id so the caller's audit-log path is exercised
/// in development without contacting Notify.
/// </summary>
internal sealed class NoOpNotifyClient(ILogger<NoOpNotifyClient> logger) : INotifyClient
{
    public Task<NotifySendResult> SendEmailAsync(
        string templateKey,
        string toEmail,
        Dictionary<string, string> personalisation,
        string reference,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "[NoOp Notify] template={TemplateKey} to={Recipient} reference={Reference} " +
            "personalisationKeys={PersonalisationKeys}",
            templateKey, toEmail, reference, string.Join(",", personalisation.Keys));
        return Task.FromResult(NotifySendResult.Success(providerMessageId: null));
    }
}
