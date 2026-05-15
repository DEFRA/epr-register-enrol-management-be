namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// The UK nation a re-accreditation application is routed to, derived from
/// the site address postcode by <see cref="NationResolver"/> (RA-125).
/// </summary>
public enum Nation
{
    England,
    Scotland,
    Wales,
    NorthernIreland
}
