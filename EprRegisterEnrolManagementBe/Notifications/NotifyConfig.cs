namespace EprRegisterEnrolManagementBe.Notifications;

/// <summary>
/// GOV.UK Notify configuration bound from the <c>Notify</c> section of
/// configuration. When <see cref="ApiKey"/> is null/empty the
/// <see cref="NoOpNotifyClient"/> is registered in place of the real
/// <see cref="GovukNotifyClient"/> so the service still boots in
/// environments where Notify credentials have not been provisioned —
/// notification calls are logged but no HTTP traffic is generated.
/// </summary>
public sealed class NotifyConfig
{
    /// <summary>
    /// API key issued by GOV.UK Notify. When null/empty the no-op client
    /// is used. Treated as a secret in production.
    /// </summary>
    public string? ApiKey { get; set; }

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
