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
/// (operator-facing) and its regulator-facing <c>SendRegulatorEmailAsync</c>
/// supply for each template. RA-581: every template's set is built and
/// declared independently here — there is no shared "base" beyond a handful
/// of keys that happen to recur, and several genuinely different-shaped
/// keys (e.g. SubmissionConfirmation's <c>SiteName</c>/<c>SiteAddress</c> vs.
/// Withdrawn's total absence of <c>organisation_name</c>) only make sense
/// read per-template rather than assumed common.
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
            // RA-581: all five operator-facing sets below are confirmed against
            // the consolidated template text pasted 2026-09-22. Each template
            // has a DIFFERENT set — there is no shared base beyond contactName
            // /reference/registration_number, and Withdrawn doesn't even have
            // organisation_name. Notify 400s on a surplus key just as readily
            // as a missing one, so these are exact, not "at least".

            // Confirmed placeholders: year, contactName, organisation_name,
            // material, SiteName, SiteAddress, regulatorName, date, reference,
            // registration_number. SiteName is never required (AC05: no
            // capture exists for the primary application's site, only
            // overseas sites) — it is in AllowedPlaceholders only.
            ["SubmissionConfirmation"] = Set(
                "year",
                "contactName",
                "organisation_name",
                "material",
                "SiteAddress",
                "regulatorName",
                "date",
                "reference",
                "registration_number"
            ),

            // Confirmed placeholders: contactName, year, organisation_name,
            // material, SubmissionDate, reference, registration_number,
            // regulatorEmail.
            ["DulyMade"] = Set(
                "contactName",
                "year",
                "organisation_name",
                "material",
                "SubmissionDate",
                "reference",
                "registration_number",
                "regulatorEmail"
            ),

            // RA-211/RA-291: queried notification, sent on the query-during-*
            // transitions. Confirmed placeholders: contactName, year,
            // organisation_name, Querieddate, reference, registration_number,
            // regulatorEmail. RA-581: operator_service_link and query_reason
            // were BOTH removed — the live template references neither.
            ["Queried"] = Set(
                "contactName",
                "year",
                "organisation_name",
                "Querieddate",
                "reference",
                "registration_number",
                "regulatorEmail"
            ),

            // RA-581: Decision was simplified to a generic "a decision has
            // been made, check the service" message — decision,
            // decision_notes, accreditation_id and accreditation_start_date
            // were ALL removed; the live template no longer references any
            // of them. Confirmed placeholders: contactName, year,
            // organisation_name, material, reference, registration_number,
            // regulatorEmail.
            ["Decision"] = Set(
                "contactName",
                "year",
                "organisation_name",
                "material",
                "reference",
                "registration_number",
                "regulatorEmail"
            ),

            // RA-204/RA-581: withdrawal notification. Deliberately the
            // thinnest of the five — no organisation_name, year, or material.
            // Confirmed placeholders: contactName, reference,
            // registration_number, withdrawal_reason.
            ["Withdrawn"] = Set(
                "contactName",
                "reference",
                "registration_number",
                "withdrawal_reason"
            ),

            // RA-581: every regulator-facing template's base set — confirmed
            // against the consolidated template text pasted 2026-09-22.
            // "Dear Regulator" is static template text in all four, not a
            // supplied placeholder. ((reference)) is the human-facing
            // RA-######### reference (see SendRegulatorEmailAsync's
            // useHumanFacingReference), and ((work_item_link)) is the
            // CaseManagementConfig-built deep link into management-fe.

            // RA-240: submission notification to the regional shared mailbox.
            // RA-581: renamed from RegulatorSubmission in Notify (same GUID).
            ["OperatorApplicationSubmission"] = Set(
                "organisation_name",
                "registration_number",
                "reference",
                "work_item_link"
            ),

            // RA-581: regulator-facing withdrawal notification, sent alongside
            // the operator's Withdrawn email. Withdrawal_reason mirrors that
            // template's own value (same latest case note, same key name).
            ["ApplicationWithdrawn"] = Set(
                "organisation_name",
                "registration_number",
                "reference",
                "Withdrawal_reason",
                "work_item_link"
            ),

            // RA-237: officer-assignment notification. RA-581: assignment_event
            // was removed — the live template no longer references it (the
            // body no longer distinguishes assigned/reassigned/unassigned),
            // and Notify 400s on a surplus key just as readily as a missing
            // one, so the hook no longer supplies it either.
            ["OfficerAssignment"] = Set(
                "organisation_name",
                "registration_number",
                "reference",
                "officer_name",
                "changed_by",
                "work_item_link"
            ),

            // RA-581: the operator has responded to a query — regulator-only,
            // no operator-facing template for this event. Base set only.
            ["QueryResponse"] = Set(
                "organisation_name",
                "registration_number",
                "reference",
                "work_item_link"
            ),
        };

    /// <summary>
    /// Template key → the FULL set of personalisation placeholders the hook is
    /// permitted to supply for that template (required keys plus any optional,
    /// conditionally-added keys). GOV.UK Notify rejects a send with a 400 not
    /// only on a MISSING required key but also on an UNEXPECTED/surplus key, so
    /// the contract test asserts the captured keys are a subset of this set.
    ///
    /// Every template's allowed set equals its required set except
    /// SubmissionConfirmation, which additionally allows the optional
    /// <c>SiteName</c> — never required (AC05: no capture exists for the
    /// primary application's site name), but the hook always supplies it as
    /// an empty string.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedPlaceholders =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["SubmissionConfirmation"] = Set(
                "year",
                "contactName",
                "organisation_name",
                "material",
                "SiteName",
                "SiteAddress",
                "regulatorName",
                "date",
                "reference",
                "registration_number"
            ),
            ["DulyMade"] = RequiredPlaceholders["DulyMade"],
            ["Queried"] = RequiredPlaceholders["Queried"],
            ["Decision"] = RequiredPlaceholders["Decision"],
            ["Withdrawn"] = RequiredPlaceholders["Withdrawn"],
            ["OperatorApplicationSubmission"] = RequiredPlaceholders["OperatorApplicationSubmission"],
            ["ApplicationWithdrawn"] = RequiredPlaceholders["ApplicationWithdrawn"],
            ["OfficerAssignment"] = RequiredPlaceholders["OfficerAssignment"],
            ["QueryResponse"] = RequiredPlaceholders["QueryResponse"],
        };

    private static IReadOnlySet<string> Set(params string[] keys) =>
        new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
}
