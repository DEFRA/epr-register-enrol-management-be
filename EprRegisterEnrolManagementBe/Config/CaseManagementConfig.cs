namespace EprRegisterEnrolManagementBe.Config;

/// <summary>
/// RA-581: configuration for the case management (management-fe) service
/// itself. Populated from the <c>CASE_MANAGEMENT_BASE_URL</c> environment
/// variable, following the same flat convention as
/// <c>OPERATOR_SERVICE_BASE_URL</c>/<c>NOTIFY_API_KEY</c>.
///
/// The base URL is threaded into every regulator-facing Notify template's
/// <c>work_item_link</c> personalisation as <c>{BaseUrl}/work-items/{id}</c>,
/// so a regulator can click straight through to the case.
///
/// Defaults to empty. An unset value is not an error: the notification hook
/// supplies an empty string for the personalisation key rather than omitting
/// it, because Notify 400s a send whose template references a placeholder the
/// caller did not supply, and a config gap must never break a regulator send.
/// </summary>
public sealed class CaseManagementConfig
{
    public string BaseUrl { get; set; } = string.Empty;
}
