namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// The UK nation a re-accreditation application is routed to. RA-526:
/// <c>ReAccreditationNationRoutingHook</c> now reads this from the caller-supplied
/// value on the submission payload (see <c>ReAccreditationPayload.Nation</c>),
/// defaulting to England when it's absent or unrecognised.
/// <see cref="NationResolver"/>'s postcode-based derivation (RA-125) is unchanged
/// but is no longer used for real submissions - only by
/// <see cref="EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReAccreditationSeeder"/>
/// for local dev fixture data.
/// </summary>
public enum Nation
{
    England,
    Scotland,
    Wales,
    NorthernIreland,
}
