namespace EprRegisterEnrolManagementBe.Config;

/// <summary>
/// RA-133: configuration for accreditation issuance. Bound from the
/// <c>Accreditation</c> configuration section. The current accreditation
/// year drives both the year segment of the generated
/// <c>AccreditationId</c> and the issued <c>AccreditationStartDate</c>
/// (1 January of the configured year, or the approval date when later).
/// </summary>
public sealed class AccreditationConfig
{
    /// <summary>
    /// Four-digit year stamped onto re-accreditation work items at
    /// approval time. Defaults to 2027 so the service is functional in
    /// development without a config override.
    /// </summary>
    public int CurrentYear { get; set; } = 2027;
}
