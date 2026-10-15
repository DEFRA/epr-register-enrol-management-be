namespace EprRegisterEnrolManagementBe.Notifications;

/// <summary>
/// Decides whether outbound GOV.UK Notify sends are actually dispatched in
/// the environment the service is running in.
///
/// <para>
/// Why this exists: the non-production Notify service is driven by a
/// <em>team</em> API key, which may only send to addresses registered on the
/// Notify team plus a hard limit of five guest addresses. Those slots are
/// exhausted. A send to any other address fails, and that failure surfaces to
/// the case worker in the case-management UI as a failed action. On dev and
/// on developer machines the email itself has no audience, so we suppress the
/// dispatch entirely rather than burn guest slots or show spurious errors.
/// </para>
///
/// <para>
/// Suppression swaps the real client for <see cref="NoOpNotifyClient"/>, which
/// returns success and logs the intended send with the same ECS shape. The
/// work item's audit log therefore still records a <c>notification-sent</c>
/// entry and the UI behaves exactly as it does in an environment that really
/// sends — the only difference is that no HTTP traffic reaches Notify.
/// </para>
/// </summary>
public static class NotifySendingPolicy
{
    /// <summary>
    /// Configuration key for the explicit override. Settable on CDP through
    /// <c>DEFRA/cdp-app-config</c> as the <c>NOTIFY_SENDEMAILS</c>
    /// environment variable — a flat top-level key, matching
    /// <c>NOTIFY_API_KEY</c>'s convention rather than the nested
    /// <c>Notify__*</c> form the rest of the Notify config binds through.
    /// </summary>
    public const string SendEmailsKey = "NOTIFY_SENDEMAILS";

    /// <summary>
    /// CDP environment names that never dispatch real email.
    /// </summary>
    private static readonly HashSet<string> NonSendingEnvironments = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "local",
        "dev",
    };

    /// <summary>
    /// Reason recorded when sends are suppressed because of the environment.
    /// </summary>
    public const string SuppressedByEnvironmentReason = "sending_disabled_for_environment";

    /// <summary>
    /// Reason recorded when sends are suppressed because no API key is set.
    /// </summary>
    public const string SuppressedByMissingApiKeyReason = "no_api_key";

    /// <summary>
    /// Whether real sends should be dispatched.
    ///
    /// <list type="number">
    /// <item>
    /// An explicit <c>NOTIFY_SENDEMAILS</c> value always wins, in both
    /// directions: it can force sending on in dev (to smoke-test the real
    /// integration against a whitelisted address) and force it off anywhere
    /// else.
    /// </item>
    /// <item>
    /// Otherwise sending is off when <c>ENVIRONMENT</c> is <c>local</c> or
    /// <c>dev</c>, or when the ASP.NET host environment is Development
    /// (which is how the Compose stack and <c>dotnet run</c> identify a
    /// developer machine — <c>ENVIRONMENT</c> is not set there).
    /// </item>
    /// <item>
    /// Otherwise sending is on. Deliberately fail-open: an unset or
    /// unrecognised <c>ENVIRONMENT</c> in a deployed environment must not
    /// silently stop test/prod notifications.
    /// </item>
    /// </list>
    /// </summary>
    /// <param name="explicitOverride">
    /// Value of <c>NOTIFY_SENDEMAILS</c>, or <c>null</c> when unset.
    /// </param>
    /// <param name="cdpEnvironment">Value of the <c>ENVIRONMENT</c> variable.</param>
    /// <param name="isDevelopmentHostEnvironment">
    /// <c>true</c> when <c>ASPNETCORE_ENVIRONMENT</c> is <c>Development</c>.
    /// </param>
    public static bool ShouldSendEmails(
        bool? explicitOverride,
        string? cdpEnvironment,
        bool isDevelopmentHostEnvironment
    )
    {
        if (explicitOverride is not null)
        {
            return explicitOverride.Value;
        }

        if (!string.IsNullOrWhiteSpace(cdpEnvironment)
            && NonSendingEnvironments.Contains(cdpEnvironment.Trim()))
        {
            return false;
        }

        return !isDevelopmentHostEnvironment;
    }
}
