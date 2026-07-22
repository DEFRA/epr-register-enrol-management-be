namespace EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

/// <summary>
/// Config for the RA-311/MBE-1 outbound push to the operator backend.
/// Mirrors the shape of the operator backend's own <c>CaseWorkingApiConfig</c>
/// (the adapter for the opposite direction) — a base URL, the client id this
/// service presents itself as, and an optional HMAC shared secret.
///
/// Bound from the <c>OperatorBackendApi</c> configuration section
/// (<c>OperatorBackendApi__Url</c> / <c>__ClientId</c> / <c>__SharedSecret</c>
/// env vars at deploy time).
/// </summary>
public sealed class OperatorBackendApiConfig
{
    public string Url { get; set; } = string.Empty;

    public string ClientId { get; set; } = "epr-register-enrol-management-be";

    public string? SharedSecret { get; set; }
}
