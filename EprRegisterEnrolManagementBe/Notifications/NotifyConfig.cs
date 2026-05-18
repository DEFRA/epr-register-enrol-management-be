namespace EprRegisterEnrolManagementBe.Notifications;

/// <summary>
/// GOV.UK Notify configuration bound from the <c>Notify</c> section of
/// configuration. The API key is read separately from the <c>NOTIFY_API_KEY</c>
/// environment variable rather than from this section — see
/// <c>ConfigureNotifications</c> in <c>Program.cs</c>.
/// </summary>
public sealed class NotifyConfig
{
    /// <summary>
    /// Optional override for the Notify base URI. Defaults to the
    /// production <c>https://api.notifications.service.gov.uk/</c> baked
    /// into the GovukNotify SDK when null/empty.
    /// </summary>
    public string? BaseUri { get; set; }

    /// <summary>
    /// Map of template keys (e.g. <c>SubmissionConfirmation</c>) to
    /// Notify template GUIDs. Modules look up template ids by key so the
    /// same code path works against Notify's preview / production
    /// services with different ids.
    /// </summary>
    public Dictionary<string, string> Templates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
