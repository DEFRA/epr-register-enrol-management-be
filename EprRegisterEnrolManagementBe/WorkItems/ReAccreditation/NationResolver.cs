using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Default <see cref="INationResolver"/>. Derives the UK nation from the
/// leading letters (area code) of a postcode.
///
/// Prefix table (authoritative source: Royal Mail postcode areas):
/// <code>
/// Northern Ireland : BT
/// Scotland         : AB DD DG EH FK G HS IV KA KW KY ML PA PH TD ZE
/// Wales            : CF CH LD LL NP SA SY
/// England          : (all others, including null / blank)
/// </code>
/// </summary>
internal sealed class NationResolver : INationResolver
{
    // Each set uses OrdinalIgnoreCase to tolerate mixed-case input.
    private static readonly HashSet<string> s_scotlandPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AB", "DD", "DG", "EH", "FK", "G", "HS", "IV", "KA", "KW",
        "KY", "ML", "PA", "PH", "TD", "ZE"
    };

    private static readonly HashSet<string> s_walesPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CF", "CH", "LD", "LL", "NP", "SA", "SY"
    };

    private const string NiPrefix = "BT";

    /// <inheritdoc />
    public Nation Resolve(string? postcode)
    {
        var area = ExtractAreaCode(postcode);
        if (area is null)
        {
            return Nation.England;
        }

        if (area.Equals(NiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Nation.NorthernIreland;
        }

        if (s_scotlandPrefixes.Contains(area))
        {
            return Nation.Scotland;
        }

        if (s_walesPrefixes.Contains(area))
        {
            return Nation.Wales;
        }

        return Nation.England;
    }

    /// <summary>
    /// Extracts the leading letter(s) (area code) from a UK postcode.
    /// Returns <c>null</c> when the postcode is null, blank, or starts
    /// with a non-letter character.
    /// </summary>
    internal static string? ExtractAreaCode(string? postcode)
    {
        if (string.IsNullOrWhiteSpace(postcode))
        {
            return null;
        }

        var trimmed = postcode.TrimStart();
        var length = 0;
        while (length < trimmed.Length && char.IsLetter(trimmed[length]))
        {
            length++;
        }

        return length == 0 ? null : trimmed[..length];
    }
}
