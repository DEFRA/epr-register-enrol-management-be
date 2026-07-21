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
/// and <c>ReAccreditationDulyMadeHook.SendDulyMadeNotificationAsync</c> supply
/// for the action that maps to each template. Optional placeholders
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
            ["SubmissionConfirmation"] = Set(
                "organisation_name",
                "registration_number",
                "reference"
            ),

            // Duly-made notification: base envelope identity only.
            ["DulyMade"] = Set("organisation_name", "registration_number", "reference"),

            // Assessment-in-progress (payment received): base envelope identity only.
            ["AssessmentInProgress"] = Set("organisation_name", "registration_number", "reference"),

            // RA-201: the SlaExtended template body additionally requires the
            // new deadline. This was the missing placeholder that caused Notify
            // to 400 the send with "Missing personalisation: sla_deadline".
            ["SlaExtended"] = Set(
                "organisation_name",
                "registration_number",
                "reference",
                "sla_deadline"
            ),

            // Decision template: base identity + the decision outcome + the
            // decision notes. RA-203: decision_notes is REQUIRED — the Decision
            // template body references ((decision_notes)) and Notify 400s the
            // send with "Missing personalisation: decision_notes" if the key is
            // absent (an empty value is permitted, so the hook always supplies
            // it). The accreditation_id / accreditation_start_date placeholders
            // remain optional (approval-only) and are intentionally not required.
            ["Decision"] = Set(
                "organisation_name",
                "registration_number",
                "reference",
                "decision",
                "decision_notes"
            ),

            // RA-211: queried notification. The template body itself (not
            // personalisation) is responsible for stating that the query
            // detail follows separately from the regulator. Sent by
            // ReAccreditationNotificationHook on the query-during-*
            // transitions (RA-291 added duly-making/duly-made).
            // RA-291 (AC06): operator_service_link is REQUIRED, not optional —
            // the hook always supplies it (empty string when
            // OPERATOR_SERVICE_BASE_URL is unset) precisely so Notify never
            // 400s the send on a missing placeholder.
            // RA-291: query_reason carries the case worker's reason through to
            // the operator — the query page promises it will. Like
            // operator_service_link it is always supplied (empty string if a
            // queried transition is somehow applied without a recorded query),
            // so Notify never 400s on a missing placeholder.
            ["Queried"] = Set(
                "organisation_name",
                "registration_number",
                "reference",
                "operator_service_link",
                "query_reason"
            ),

            // RA-240: regulator-facing submission notification to the regional
            // shared mailbox. Base envelope identity only.
            ["RegulatorSubmission"] = Set("organisation_name", "registration_number", "reference"),

            // RA-237: officer-assignment notification to the regional regulator
            // shared mailbox. Base envelope identity plus the assignment event
            // description, the officer name, and who made the change. All three
            // extra keys are always supplied (empty-string defaults) so they are
            // required, not optional.
            ["OfficerAssignment"] = Set(
                "organisation_name",
                "registration_number",
                "reference",
                "assignment_event",
                "officer_name",
                "changed_by"
            ),
        };

    /// <summary>
    /// Template key → the FULL set of personalisation placeholders the hook is
    /// permitted to supply for that template (required keys plus any optional,
    /// conditionally-added keys). GOV.UK Notify rejects a send with a 400 not
    /// only on a MISSING required key but also on an UNEXPECTED/surplus key, so
    /// the contract test asserts the captured keys are a subset of this set.
    ///
    /// For every template except Decision the allowed set equals the required
    /// set. The Decision template additionally allows the optional
    /// <c>accreditation_id</c> / <c>accreditation_start_date</c> placeholders
    /// that the hook stamps on the approve path.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedPlaceholders =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["SubmissionConfirmation"] = RequiredPlaceholders["SubmissionConfirmation"],
            ["DulyMade"] = RequiredPlaceholders["DulyMade"],
            ["AssessmentInProgress"] = RequiredPlaceholders["AssessmentInProgress"],
            ["SlaExtended"] = RequiredPlaceholders["SlaExtended"],
            ["Decision"] = Set(
                "organisation_name",
                "registration_number",
                "reference",
                "decision",
                "decision_notes",
                "accreditation_id",
                "accreditation_start_date"
            ),
            ["Queried"] = RequiredPlaceholders["Queried"],
            ["RegulatorSubmission"] = RequiredPlaceholders["RegulatorSubmission"],
            ["OfficerAssignment"] = RequiredPlaceholders["OfficerAssignment"],
        };

    private static IReadOnlySet<string> Set(params string[] keys) =>
        new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
}
