using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Derives the UK nation for a re-accreditation application from the site
/// address postcode (RA-125). Rules are based on well-known postcode area
/// prefixes:
/// <list type="bullet">
///   <item>Northern Ireland — <c>BT</c></item>
///   <item>Scotland — <c>AB</c>, <c>DD</c>, <c>DG</c>, <c>EH</c>, <c>FK</c>,
///     <c>G</c>, <c>HS</c>, <c>IV</c>, <c>KA</c>, <c>KW</c>, <c>KY</c>,
///     <c>ML</c>, <c>PA</c>, <c>PH</c>, <c>TD</c>, <c>ZE</c></item>
///   <item>Wales — <c>CF</c>, <c>CH</c>, <c>LD</c>, <c>LL</c>, <c>NP</c>,
///     <c>SA</c>, <c>SY</c> (partial — included for broad coverage)</item>
///   <item>England — everything else</item>
/// </list>
/// A <c>null</c> or unrecognisable postcode defaults to
/// <see cref="Nation.England"/> (fail-open rather than blocking
/// submission).
/// </summary>
public interface INationResolver
{
    /// <summary>
    /// Returns the <see cref="Nation"/> corresponding to
    /// <paramref name="postcode"/>, defaulting to
    /// <see cref="Nation.England"/> when the postcode is null, blank, or
    /// does not match a known area prefix.
    /// </summary>
    Nation Resolve(string? postcode);
}
