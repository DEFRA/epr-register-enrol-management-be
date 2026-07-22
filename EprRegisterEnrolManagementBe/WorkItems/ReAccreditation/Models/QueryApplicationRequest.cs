namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// RA-291 request body for
/// <c>POST /work-items/re-accreditation/{id}/query</c>.
///
/// Both members are nullable so a structurally-broken body reaches
/// <see cref="ReAccreditationQueryValidator"/> and is rejected with a
/// ProblemDetails 400 rather than a binding failure — the caller gets the
/// same message shape whether they omitted <c>sections</c> or sent an
/// unknown section id.
/// </summary>
internal sealed record QueryApplicationRequest(IReadOnlyList<string>? Sections, string? Reason);
