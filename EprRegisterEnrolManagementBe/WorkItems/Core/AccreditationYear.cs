using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// The one reader of <c>payload.accreditationYear</c> for the reference
/// generators (RA-492). Both the AP and IYC formats read it here, so they agree
/// on what counts as a usable year.
/// </summary>
internal static class AccreditationYear
{
    /// <summary>Payload field holding the 4-digit accreditation year.</summary>
    public const string Field = "accreditationYear";

    /// <summary>
    /// True when <paramref name="payload"/> carries a usable year: a numeric
    /// whole number from 1000 to 9999. A string such as <c>"2026"</c> is not
    /// usable; the operator backend sends an int.
    /// </summary>
    public static bool TryRead(BsonDocument payload, out int year)
    {
        ArgumentNullException.ThrowIfNull(payload);
        year = 0;
        return payload.TryGetValue(Field, out var value)
            && BsonWholeNumber.TryGet(value, 1000, 9999, out year);
    }
}
