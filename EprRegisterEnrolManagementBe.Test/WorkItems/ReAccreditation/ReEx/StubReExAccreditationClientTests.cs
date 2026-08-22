using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation.ReEx;

/// <summary>
/// The stub client always returns a fixed prior-year record regardless of
/// the identifiers passed, so local/CI environments without ReEx
/// credentials can still exercise the application-details page.
/// </summary>
public class StubReExAccreditationClientTests
{
    [Fact]
    public async Task GetPriorYearAsync_returns_a_fixed_record_for_any_identifiers()
    {
        var client = new StubReExAccreditationClient();

        var result = await client.GetPriorYearAsync("org-1", "reg-1", 2025);

        Assert.NotNull(result);
        Assert.Equal(2025, result!.Year);
        Assert.Equal("UpTo5000", result.TonnageBand);
        Assert.Single(result.Authorisers);
        Assert.Equal("Jane Stub", result.Authorisers[0].FullName);
        Assert.Equal("jane.stub@example.com", result.Authorisers[0].Email);
        Assert.NotNull(result.BusinessPlan);
    }

    [Fact]
    public async Task GetPriorYearAsync_returns_all_seven_business_plan_categories_summing_to_100()
    {
        // RA-456: the stub previously omitted OtherPercent (the "Activities
        // or investment not covered by the other categories" category),
        // leaving it inconsistent with HttpReExAccreditationClient's
        // s_businessPlanMap, which already maps all seven. Pin both that the
        // field is populated and that every percentage still sums to 100.
        var client = new StubReExAccreditationClient();

        var result = await client.GetPriorYearAsync("org-1", "reg-1", 2025);

        Assert.NotNull(result);
        var plan = result!.BusinessPlan;
        Assert.NotNull(plan.OtherPercent);

        var total = (plan.NewInfrastructurePercent ?? 0)
            + (plan.PriceSupportPercent ?? 0)
            + (plan.BusinessCollectionsPercent ?? 0)
            + (plan.CommunicationsPercent ?? 0)
            + (plan.NewMarketsPercent ?? 0)
            + (plan.NewUsesPercent ?? 0)
            + (plan.OtherPercent ?? 0);
        Assert.Equal(100, total);
    }

    [Fact]
    public async Task GetPriorYearAsync_defaults_the_year_to_the_previous_calendar_year_when_null()
    {
        var client = new StubReExAccreditationClient();

        var result = await client.GetPriorYearAsync(null, null, null);

        Assert.NotNull(result);
        Assert.Equal(DateTime.UtcNow.Year - 1, result!.Year);
    }

    [Fact]
    public async Task GetPriorYearAsync_honours_a_cancellation_token_parameter()
    {
        var client = new StubReExAccreditationClient();
        using var cts = new CancellationTokenSource();

        var result = await client.GetPriorYearAsync("org-1", "reg-1", 2024, cts.Token);

        Assert.NotNull(result);
        Assert.Equal(2024, result!.Year);
    }
}
