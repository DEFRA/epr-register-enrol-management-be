using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx.Dtos;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx;

/// <summary>
/// Development stub: always returns a fixed prior-year record regardless of
/// the identifiers passed, so the application-details page can be exercised
/// locally without ReEx credentials. Registered when
/// <c>REEX_API_BASIC_AUTH_USERNAME</c> is absent.
/// </summary>
internal sealed class StubReExAccreditationClient : IReExAccreditationClient
{
    public Task<PriorYearAccreditationDto?> GetPriorYearAsync(
        string? organisationId,
        string? registrationId,
        int? year,
        CancellationToken cancellationToken = default)
    {
        var priorYear = year ?? DateTime.UtcNow.Year - 1;

        return Task.FromResult<PriorYearAccreditationDto?>(new PriorYearAccreditationDto
        {
            Year = priorYear,
            TonnageBand = "UpTo1000",
            Authorisers =
            [
                new PriorYearAuthoriserDto
                {
                    FullName = "Jane Stub",
                    Email = "jane.stub@example.com"
                }
            ],
            BusinessPlan = new PriorYearBusinessPlanDto
            {
                NewInfrastructurePercent = 20,
                PriceSupportPercent = 20,
                BusinessCollectionsPercent = 20,
                CommunicationsPercent = 20,
                NewMarketsPercent = 10,
                NewUsesPercent = 10
            }
        });
    }
}
