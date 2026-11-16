namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// RA-766 (temporary) request body for
/// <c>PUT /work-items/re-accreditation/{id}/charge</c>. Nullable so a missing
/// value reaches the handler and is a ProblemDetails 400, not a binding failure.
/// </summary>
internal sealed record SetChargeRequest(int? ChargeAmountPence);

/// <summary>RA-766 (temporary) response body of the charge correction endpoint.</summary>
internal sealed record SetChargeResponse(string Outcome, int ChargeAmountPence, bool DulyMade);
