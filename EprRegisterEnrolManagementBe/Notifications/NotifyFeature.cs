namespace EprRegisterEnrolManagementBe.Notifications;

/// <summary>
/// RA-422: single source of truth for the central "are Case Management
/// notifications enabled?" decision. Both wiring sites that gate email +
/// notification-audit behaviour consult this one helper, so the toggle can
/// never drift between them:
/// <list type="bullet">
///   <item><c>ConfigureNotifications</c> in <c>Program.cs</c> registers the
///   no-op Notify client when disabled, so no real email can leave even if
///   something resolves <see cref="INotifyClient"/> directly.</item>
///   <item><c>ReAccreditationModule</c> registers the notification
///   post-action hook only when enabled, so when disabled no email is sent
///   AND no <c>notification-sent/skipped/failed</c> audit entries are
///   written.</item>
/// </list>
/// Consulted at service-registration time (before the options graph is
/// built), so it reads raw configuration rather than a bound
/// <see cref="NotifyConfig"/> — mirroring how <c>ConfigureOperatorBackendPush</c>
/// reads <c>OperatorBackendApi:Enabled</c>.
/// </summary>
internal static class NotifyFeature
{
    /// <summary>Configuration key backing <see cref="NotifyConfig.Enabled"/>.</summary>
    internal const string EnabledConfigKey = "Notify:Enabled";

    /// <summary>
    /// <c>true</c> only when <c>Notify:Enabled</c> is explicitly configured
    /// true; defaults to <c>false</c> (emails off) when the key is absent.
    /// </summary>
    internal static bool NotificationsEnabled(IConfiguration configuration) =>
        configuration.GetValue(EnabledConfigKey, false);
}
