using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

namespace EprRegisterEnrolManagementBe.WorkItems.InYearChanges;

/// <summary>
/// RA-769: what <c>GET /work-items/{id}/accreditation-summary</c> answers: the accreditation as the operator
/// backend holds it, and the date it started.
/// </summary>
/// <param name="Accreditation">The operator backend's summary, unchanged.</param>
/// <param name="AccreditationStartDate">The start date stamped on the Granted re-accreditation work item that issued
/// the accreditation, else 1 January of the accreditation year.</param>
public sealed record AccreditationSummaryResponse(
    OperatorAccreditationSummary Accreditation,
    DateOnly AccreditationStartDate
);
