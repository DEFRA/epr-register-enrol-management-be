using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Reads a bounded whole number from a numeric BSON value, for the reference
/// generators' payload fields (<c>operatorOrgNumber</c>, <c>accreditationYear</c>).
/// </summary>
internal static class BsonWholeNumber
{
    /// <summary>
    /// True when <paramref name="value"/> is numeric, whole and within
    /// <paramref name="min"/>..<paramref name="max"/> inclusive.
    /// </summary>
    /// <remarks>
    /// <c>IsNumeric</c> admits BsonInt64, BsonDouble and BsonDecimal128.
    /// <c>BsonValue.ToInt32()</c> cannot be trusted to convert any of them
    /// safely: it silently wraps an out-of-range Int64 or Double (for example
    /// <c>long.MaxValue</c> narrows to -1), and it throws OverflowException for
    /// Decimal128. So the value is validated explicitly through decimal.
    /// Decimal is an exact fixed-point type, so the integrality check is
    /// precise (it is not the float equality SonarCloud S1244 warns about).
    /// The conversion also fails cleanly for NaN and Infinity.
    /// </remarks>
    public static bool TryGet(BsonValue value, int min, int max, out int result)
    {
        result = 0;
        if (!value.IsNumeric)
        {
            return false;
        }

        decimal asDecimal;
        try
        {
            asDecimal = value.ToDecimal();
        }
        catch (Exception ex)
            when (ex is OverflowException or FormatException or InvalidCastException)
        {
            return false;
        }

        if (asDecimal < min || asDecimal > max || asDecimal != Math.Truncate(asDecimal))
        {
            return false;
        }

        result = (int)asDecimal;
        return true;
    }
}
