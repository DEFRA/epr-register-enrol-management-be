using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

namespace EprRegisterEnrolManagementBe.WorkItems.InYearChanges;

/// <summary>
/// RA-769: what <c>GET /work-items/{id}/accreditation-summary</c> answers: the accreditation as the operator
/// backend holds it, and the date it started.
/// </summary>
/// <param name="Accreditation">The operator backend's summary, unchanged.</param>
/// <param name="AccreditationStartDate">1 January of the accreditation year for now; the stored start date
/// replaces it in RA-769 task 003.</param>
public sealed record AccreditationSummaryResponse(
    OperatorAccreditationSummary Accreditation,
    DateOnly AccreditationStartDate
);
