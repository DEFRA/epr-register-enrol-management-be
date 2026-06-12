namespace EprRegisterEnrolManagementBe.Test.Notifications;

/// <summary>
/// RA-201: declarative contract describing the set of personalisation
/// placeholders that each GOV.UK Notify template REQUIRES in its body.
///
/// This is the single source of truth the contract tests assert against:
/// <list type="bullet">
///   <item><see cref="NotifyTemplateContractTests"/> drives
///   <c>ReAccreditationNotificationHook</c> for every lifecycle action and
///   asserts the personalisation it produces is a superset of the required
///   keys here — the regression guard that would have caught the missing
///   <c>sla_deadline</c> placeholder that broke the extend-SLA email.</item>
///   <item><see cref="NotifyLiveTemplateContractTests"/> (opt-in) fetches the
///   real template bodies from Notify and asserts their <c>((token))</c>
///   placeholders match the required set here.</item>
/// </list>
///
/// Keys reflect what <c>ReAccreditationNotificationHook.BuildPersonalisation</c>
/// supplies for the action that maps to each template. Optional placeholders
/// that the hook only adds conditionally (e.g. the Decision template's
/// <c>accreditation_id</c> / <c>accreditation_start_date</c>, which are only
/// stamped after an approval) are NOT listed as required, so the superset
/// assertion does not demand them on the reject path.
/// </summary>
internal static class NotifyTemplateContract
{
    /// <summary>
    /// Template key (matching the <c>Notify:Templates</c> config keys and the
    /// keys passed to <c>INotifyClient.SendEmailAsync</c>) → the required
    /// personalisation placeholder names for that template.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> RequiredPlaceholders =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            // Submission confirmation: base envelope identity only.
            ["SubmissionConfirmation"] = Set("organisation_name", "registration_number", "reference"),

            // Duly-made notification: base envelope identity only.
            ["DulyMade"] = Set("organisation_name", "registration_number", "reference"),

            // Assessment-in-progress (payment received): base envelope identity only.
            ["AssessmentInProgress"] = Set("organisation_name", "registration_number", "reference"),

            // RA-201: the SlaExtended template body additionally requires the
            // new deadline. This was the missing placeholder that caused Notify
            // to 400 the send with "Missing personalisation: sla_deadline".
            ["SlaExtended"] = Set("organisation_name", "registration_number", "reference", "sla_deadline"),

            // Decision template: base identity + the decision outcome. The
            // accreditation_id / accreditation_start_date placeholders are
            // optional (approval-only) and so are intentionally not required.
            ["Decision"] = Set("organisation_name", "registration_number", "reference", "decision"),
        };

    private static IReadOnlySet<string> Set(params string[] keys) =>
        new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
}
