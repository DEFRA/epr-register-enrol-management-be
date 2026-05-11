namespace EprRegisterEnrolManagementBe.Notifications;

/// <summary>
/// Outbound notification abstraction. Wraps GOV.UK Notify behind a
/// project-owned interface so callers depend on a stable shape, the
/// implementation can be swapped (real / no-op / test fake), and the
/// underlying SDK choice never leaks into modules.
/// </summary>
public interface INotifyClient
{
    /// <summary>
    /// Send an email by template key. The key is resolved to a Notify
    /// template GUID via <see cref="NotifyConfig.Templates"/>.
    ///
    /// Returns a result rather than throwing on transport / API
    /// failures so callers can record an audit entry without unwinding
    /// the work item state change that triggered the send. A missing
    /// template key returns <see cref="NotifySendResult.Failure"/> with
    /// an error message — it is a misconfiguration, not an exception.
    /// </summary>
    Task<NotifySendResult> SendEmailAsync(
        string templateKey,
        string toEmail,
        Dictionary<string, string> personalisation,
        string reference,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of a single send attempt.
/// </summary>
public sealed record NotifySendResult(
    bool IsSuccess,
    string? ProviderMessageId,
    string? ErrorMessage)
{
    public static NotifySendResult Success(string? providerMessageId) =>
        new(true, providerMessageId, null);

    public static NotifySendResult Failure(string errorMessage) =>
        new(false, null, errorMessage);
}
