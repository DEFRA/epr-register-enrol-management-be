namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// RA-480: the submitter's contact details, captured on the case
/// management "additional information" tab and stamped onto
/// <c>payload.submitterContactDetails</c>. RA-581 surfaces
/// <see cref="FullName"/> as the <c>contact_name</c> Notify personalisation
/// placeholder (falling back to the organisation name when blank — see
/// <c>ReAccreditationNotificationHook.BuildPersonalisation</c>); the other
/// fields are not currently used by any template.
/// </summary>
internal sealed record SubmitterContactDetails
{
    public string? FullName { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? JobTitle { get; init; }
}
