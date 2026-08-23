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
            TonnageBand = "UpTo5000",
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
                NewInfrastructurePercent = 15,
                PriceSupportPercent = 15,
                BusinessCollectionsPercent = 15,
                CommunicationsPercent = 15,
                NewMarketsPercent = 10,
                NewUsesPercent = 10,
                // RA-456: the 7th "Activities or investment not covered by the
                // other categories" category — matches HttpReExAccreditationClient's
                // s_businessPlanMap entry for the same category, and the other
                // six percentages above were rebalanced (20/20/20/20/10/10 ->
                // 15/15/15/15/10/10) so all seven still sum to 100.
                OtherPercent = 20
            }
        });
    }
}
