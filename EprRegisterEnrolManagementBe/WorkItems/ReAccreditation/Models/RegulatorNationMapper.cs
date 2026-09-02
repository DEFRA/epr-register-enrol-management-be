namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// RA-526: maps a ReEx regulator code (<c>ReExRegistrationDto.SubmittedToRegulator</c>) to a
/// <see cref="Nation"/>. Mirrors epr-register-enrol-backend's own
/// <c>RegulatorNationMapper</c> exactly — this is the source
/// <see cref="ReAccreditationNationCorrectionMigration"/> uses to independently verify /
/// correct a work item's <c>payload.nation</c> for applications submitted before that
/// backend derived it, rather than deriving it a second, possibly-drifting way.
/// </summary>
internal static class RegulatorNationMapper
{
    private static readonly Dictionary<string, Nation> CodeToNation = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["ea"] = Nation.England,
        ["nrw"] = Nation.Wales,
        ["sepa"] = Nation.Scotland,
        ["niea"] = Nation.NorthernIreland,
    };

    /// <summary>
    /// Maps a regulator code to a Nation, defaulting to England when the code is null/blank
    /// or unrecognised. Returns false only for the unrecognised-non-null case, so the caller
    /// can log a warning and keep the gap observable — null/blank is treated as the expected
    /// default, not a data gap.
    /// </summary>
    public static bool TryMap(string? regulatorCode, out Nation nation)
    {
        if (string.IsNullOrWhiteSpace(regulatorCode))
        {
            nation = Nation.England;
            return true;
        }

        if (CodeToNation.TryGetValue(regulatorCode, out var mapped))
        {
            nation = mapped;
            return true;
        }

        nation = Nation.England;
        return false;
    }
}
