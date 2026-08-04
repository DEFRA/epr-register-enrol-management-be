using EprRegisterEnrolManagementBe.Utils.Logging;
using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolManagementBe.Notifications;

/// <summary>
/// Non-dispatching <see cref="INotifyClient"/>. Registered when
/// <c>NOTIFY_API_KEY</c> is absent or empty, and when
/// <see cref="NotifySendingPolicy"/> says the current environment must not
/// send real email (dev / localhost — see that class for why). Logs the
/// intended send via <see cref="IStructuredLogger{T}"/> using the same
/// <c>event.category=notify</c> / <c>event.action=send_email</c>
/// shape as <see cref="GovukNotifyClient"/> so local / non-Notify
/// environments expose the same OpenSearch shape as the real client,
/// and returns success with a null provider message id so the
/// caller's audit-log path is exercised in development without
/// contacting Notify.
/// </summary>
/// <param name="suppressionReason">
/// Emitted as <c>notify.suppression_reason</c> so a log reader can tell
/// "no API key configured" apart from "this environment deliberately does
/// not send" — see <see cref="NotifySendingPolicy"/> for the values.
/// </param>
internal sealed class NoOpNotifyClient(
    IStructuredLogger<NoOpNotifyClient> log,
    string suppressionReason = NotifySendingPolicy.SuppressedByMissingApiKeyReason
) : INotifyClient
{
    public Task<NotifySendResult> SendEmailAsync(
        string templateKey,
        string toEmail,
        Dictionary<string, string> personalisation,
        string reference,
        string? region = null,
        CancellationToken cancellationToken = default
    )
    {
        log.Log(
            LogLevel.Information,
            "Notify send skipped (no-op client)",
            new Dictionary<string, object?>
            {
                ["event.category"] = GovukNotifyClient.EventCategory,
                ["event.action"] = GovukNotifyClient.EventAction,
                ["event.outcome"] = "success",
                ["event.duration"] = 0L,
                ["event.reference"] = reference,
                ["notify.template_key"] = templateKey,
                ["notify.region"] = region,
                ["notify.suppression_reason"] = suppressionReason,
            }
        );
        return Task.FromResult(NotifySendResult.Success(providerMessageId: null));
    }
}
