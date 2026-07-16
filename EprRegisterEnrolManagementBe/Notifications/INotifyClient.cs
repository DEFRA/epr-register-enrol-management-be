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
    /// <param name="region">
    /// RA-211: region/regulator identifier (e.g. a
    /// <c>ReAccreditationPayload.Nation</c> value such as <c>England</c>)
    /// used to resolve the Notify <c>reply_to_id</c> via
    /// <see cref="NotifyConfig.GetReplyToId"/>. <c>null</c>/unrecognised
    /// falls back to <see cref="NotifyConfig.DefaultReplyToId"/>.
    /// </param>
    Task<NotifySendResult> SendEmailAsync(
        string templateKey,
        string toEmail,
        Dictionary<string, string> personalisation,
        string reference,
        string? region = null,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Outcome of a single send attempt.
/// </summary>
public sealed record NotifySendResult(
    bool IsSuccess,
    string? ProviderMessageId,
    string? ErrorMessage
)
{
    public static NotifySendResult Success(string? providerMessageId) =>
        new(true, providerMessageId, null);

    public static NotifySendResult Failure(string errorMessage) => new(false, null, errorMessage);
}
