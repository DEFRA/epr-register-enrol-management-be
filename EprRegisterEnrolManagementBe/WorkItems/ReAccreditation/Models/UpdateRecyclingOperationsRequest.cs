namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// RA-469 request body for
/// <c>PATCH /work-items/re-accreditation/{id}/overseas-sites/{siteId}/recycling-operations</c>.
///
/// Deliberately a thin pass-through: the R3/R4/R5/R12/R13 accompanying-code
/// rule, material-type applicability, and "at least one code" checks
/// (AC10–AC12) are enforced server-side on epr-register-enrol-backend (the
/// system of record for <c>OverseasSiteModel.OperationCodes</c>), not
/// duplicated here — a missing/empty <see cref="OperationCodes"/> is still
/// forwarded and rejected by the backend with a machine-readable
/// errorCode/field, the same as any other validation failure.
/// </summary>
public sealed record UpdateRecyclingOperationsRequest
{
    public IReadOnlyList<string>? OperationCodes { get; init; }
}
