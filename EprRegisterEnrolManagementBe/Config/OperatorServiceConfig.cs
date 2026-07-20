namespace EprRegisterEnrolManagementBe.Config;

/// <summary>
/// RA-291 (AC06): configuration for the public-facing operator service.
/// Bound from the <c>OperatorService</c> configuration section; override in
/// a deployed environment with <c>OperatorService__BaseUrl</c>.
///
/// The base URL is threaded into the <c>Queried</c> Notify template's
/// <c>operator_service_link</c> personalisation so a queried operator can
/// navigate back to their application. It is deliberately NOT used to build
/// per-section deep links — RA-291 scopes those out.
///
/// Defaults to empty. An unset value is not an error: the notification hook
/// supplies an empty string for the personalisation key rather than omitting
/// it, because Notify 400s a send whose template references a placeholder the
/// caller did not supply, and a config gap must never break the query flow.
/// </summary>
public sealed class OperatorServiceConfig
{
    public string BaseUrl { get; set; } = string.Empty;
}
