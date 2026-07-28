namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// RA-252 request body for
/// <c>POST /work-items/re-accreditation/{id}/withdraw</c>.
///
/// Nullable so a structurally-broken body reaches
/// <see cref="ReAccreditationWithdrawValidator"/> and is rejected with a
/// ProblemDetails 400 rather than a binding failure — mirrors
/// <see cref="QueryApplicationRequest"/>.
/// </summary>
internal sealed record WithdrawApplicationRequest(string? Reason);
