using System.Security.Cryptography;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-132 default <see cref="IAccreditationIdGenerator"/>. Produces ids of
/// the shape <c>RA-XXXXXXXX</c> where the suffix is eight uppercase hex
/// characters derived from four cryptographically random bytes. 32 bits of
/// entropy is enough to make accidental collisions across the lifetime of
/// the service vanishingly unlikely while keeping the printed id short.
/// </summary>
internal sealed class AccreditationIdGenerator : IAccreditationIdGenerator
{
    public string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(4);
        return "RA-" + Convert.ToHexString(bytes);
    }
}
