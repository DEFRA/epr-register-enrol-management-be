using System.Text.Json.Serialization;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// RA-307: the glass recycling process recorded on an accreditation, present
/// only when the application's material is glass. Deliberately a closed
/// two-value set rather than a free string, mirroring the enum of the same
/// name in epr-register-enrol-backend.
///
/// Member names are spelled to match the wire value verbatim (glass_re_melt,
/// glass_other) rather than PascalCase — like <see cref="ReAccreditationDecisionOutcome.Rejected"/>,
/// this prioritises matching the external id over C# convention. That is not
/// cosmetic: this record is deserialised from the stored work-item payload
/// both via System.Text.Json (JsonStringEnumConverter) and directly via the
/// MongoDB driver's default string-by-member-name enum representation —
/// matching the wire value exactly means both paths round-trip correctly
/// with no custom converter needed on either side.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum GlassRecyclingProcess
{
    glass_re_melt,
    glass_other,
}
