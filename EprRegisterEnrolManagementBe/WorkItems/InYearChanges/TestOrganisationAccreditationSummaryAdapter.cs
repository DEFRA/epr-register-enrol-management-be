using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

namespace EprRegisterEnrolManagementBe.WorkItems.InYearChanges;

/// <summary>
/// RA-769: registered only when <c>TestOrganisations__Enabled</c> is on (see
/// <see cref="AccreditationSummaryEndpoints.AddAccreditationSummaryServices"/>), so no test branch sits on a
/// production request path. Reads for an end-to-end test organisation
/// (<see cref="TestOrganisations.IsTestOrganisation"/>) are answered from the stable
/// <see cref="TestOrganisationAccreditationSummary.Fixture"/>, with no operator backend call: the operator backend has
/// never heard of them, and a case posted directly to this service has no application id. Every other organisation
/// goes to <paramref name="real"/>. The same shape as <c>TestOrganisationReExAccreditationClient</c>.
/// </summary>
internal sealed class TestOrganisationAccreditationSummaryAdapter(IAccreditationSummaryAdapter real)
    : IAccreditationSummaryAdapter
{
    public Task<AccreditationSummaryReadResult> GetAccreditationSummaryAsync(
        AccreditationSummaryReadRequest request,
        CancellationToken cancellationToken = default
    ) =>
        TestOrganisations.IsTestOrganisation(request.OrganisationId)
            ? Task.FromResult(AccreditationSummaryReadResult.Success(TestOrganisationAccreditationSummary.Fixture))
            : real.GetAccreditationSummaryAsync(request, cancellationToken);
}
