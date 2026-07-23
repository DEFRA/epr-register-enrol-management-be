namespace EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

/// <summary>
/// Config for the RA-311/MBE-1 outbound push to the operator backend.
/// Mirrors the shape of the operator backend's own <c>CaseWorkingApiConfig</c>
/// (the adapter for the opposite direction) — a base URL, the client id this
/// service presents itself as, and an optional HMAC shared secret.
///
/// Bound from the <c>OperatorBackendApi</c> configuration section
/// (<c>OperatorBackendApi__Enabled</c> / <c>__Url</c> / <c>__ClientId</c> /
/// <c>__SharedSecret</c> env vars at deploy time). Validated on start by
/// <see cref="OperatorBackendApiConfigValidator"/>: <see cref="Url"/>,
/// <see cref="ClientId"/> and <see cref="SharedSecret"/> are only required
/// when <see cref="Enabled"/> is <c>true</c>.
/// </summary>
public sealed class OperatorBackendApiConfig
{
    /// <summary>
    /// Master switch for the outbound push (MBE-F5). Defaults to
    /// <c>false</c> so deploying this code is behaviour-neutral until it is
    /// explicitly turned on per environment, and doubles as the rollback
    /// lever — flip back to <c>false</c> to disable the push without a code
    /// deploy.
    /// </summary>
    public bool Enabled { get; set; }

    public string Url { get; set; } = string.Empty;

    public string ClientId { get; set; } = "epr-register-enrol-management-be";

    public string? SharedSecret { get; set; }
}