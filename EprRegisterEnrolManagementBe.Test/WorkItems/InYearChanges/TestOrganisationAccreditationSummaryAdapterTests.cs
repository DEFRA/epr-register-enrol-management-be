using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.InYearChanges;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.InYearChanges;

/// <summary>
/// RA-769: the decorator that answers <c>e2e-</c> organisations from the fixture and sends every other organisation
/// to the real adapter. Whether it is registered at all is <c>TestOrganisationsWiringTests</c>.
/// </summary>
public class TestOrganisationAccreditationSummaryAdapterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IAccreditationSummaryAdapter _real = Substitute.For<IAccreditationSummaryAdapter>();
    private readonly TestOrganisationAccreditationSummaryAdapter _adapter;

    public TestOrganisationAccreditationSummaryAdapterTests()
    {
        _adapter = new TestOrganisationAccreditationSummaryAdapter(_real);
    }

    [Theory]
    [InlineData("app-1")]
    [InlineData(null)]
    public async Task An_e2e_organisation_is_answered_from_the_fixture_without_calling_the_real_adapter(
        string? applicationId
    )
    {
        var result = await _adapter.GetAccreditationSummaryAsync(
            new AccreditationSummaryReadRequest("e2e-3f2a9c", applicationId, Guid.NewGuid()),
            Ct
        );

        Assert.Equal(AccreditationSummaryReadOutcome.Success, result.Outcome);
        Assert.Same(TestOrganisationAccreditationSummary.Fixture, result.Summary);
        await _real.DidNotReceiveWithAnyArgs().GetAccreditationSummaryAsync(default!, Ct);
    }

    [Theory]
    [InlineData("500027")]
    [InlineData("E2E-3f2a9c")]
    [InlineData("org-e2e-3f2a9c")]
    [InlineData(null)]
    public async Task Every_other_organisation_goes_to_the_real_adapter(string? organisationId)
    {
        var request = new AccreditationSummaryReadRequest(organisationId, "app-1", Guid.NewGuid());
        var expected = AccreditationSummaryReadResult.TransientFailure("operator backend is down");
        _real.GetAccreditationSummaryAsync(request, Ct).Returns(expected);

        var result = await _adapter.GetAccreditationSummaryAsync(request, Ct);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task The_fixture_is_usable_as_a_summary()
    {
        var result = await _adapter.GetAccreditationSummaryAsync(
            new AccreditationSummaryReadRequest("e2e-x", null, Guid.NewGuid()),
            Ct
        );

        Assert.True(result.Summary!.IsUsable);
        Assert.Equal(2026, result.Summary.AccreditationYear);
    }
}
