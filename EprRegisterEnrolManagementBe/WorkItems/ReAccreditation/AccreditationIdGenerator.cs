using System.Security.Cryptography;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-132 default <see cref="IAccreditationIdGenerator"/>. Produces ids of
/// the shape <c>RA-XXXXXXXXXXXXXXXX</c> where the suffix is sixteen uppercase
/// hex characters derived from eight cryptographically random bytes (64 bits
/// of entropy). At one million issued ids the birthday-collision probability
/// is ~2.7e-8, making accidental duplicates negligible across the lifetime of
/// the service.
/// </summary>
internal sealed class AccreditationIdGenerator : IAccreditationIdGenerator
{
    public string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(8);
        return "RA-" + Convert.ToHexString(bytes);
    }
}
