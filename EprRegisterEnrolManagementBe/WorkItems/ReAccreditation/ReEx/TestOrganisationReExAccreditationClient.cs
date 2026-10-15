using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx.Dtos;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx;

/// <summary>
/// RA-000: registered only when <c>TestOrganisations__Enabled</c> is on and
/// real ReEx credentials are configured (CDP test). Lookups for an
/// end-to-end test organisation (<see cref="TestOrganisations.IsTestOrganisation"/>)
/// go to <paramref name="testOrganisations"/> — the stub, since real ReEx
/// 404s organisations the journey suite invented — so the prior-year section
/// renders for them exactly as it does on the compose stack. Every other
/// organisation goes to <paramref name="real"/>.
/// </summary>
internal sealed class TestOrganisationReExAccreditationClient(
    IReExAccreditationClient real,
    IReExAccreditationClient testOrganisations
) : IReExAccreditationClient
{
    public Task<PriorYearAccreditationDto?> GetPriorYearAsync(
        string? organisationId,
        string? registrationId,
        int? year,
        CancellationToken cancellationToken = default
    ) =>
        Route(organisationId)
            .GetPriorYearAsync(organisationId, registrationId, year, cancellationToken);

    public Task<Nation?> GetNationAsync(
        string? organisationId,
        string? registrationId,
        CancellationToken cancellationToken = default
    ) => Route(organisationId).GetNationAsync(organisationId, registrationId, cancellationToken);

    private IReExAccreditationClient Route(string? organisationId) =>
        TestOrganisations.IsTestOrganisation(organisationId) ? testOrganisations : real;
}
