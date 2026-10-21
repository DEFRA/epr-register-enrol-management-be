using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx.Dtos;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation.ReEx;

public class TestOrganisationReExAccreditationClientTests
{
    private readonly IReExAccreditationClient _real = Substitute.For<IReExAccreditationClient>();
    private readonly IReExAccreditationClient _stub = Substitute.For<IReExAccreditationClient>();
    private readonly TestOrganisationReExAccreditationClient _client;

    public TestOrganisationReExAccreditationClientTests()
    {
        _client = new TestOrganisationReExAccreditationClient(_real, _stub);
    }

    [Fact]
    public async Task GetPriorYearAsync_sends_an_e2e_organisation_to_the_stub()
    {
        var ct = TestContext.Current.CancellationToken;
        var record = new PriorYearAccreditationDto { Year = 2025 };
        _stub.GetPriorYearAsync("e2e-3f2a9c", "e2e-reg-3f2a9c", 2025, ct).Returns(record);

        var result = await _client.GetPriorYearAsync("e2e-3f2a9c", "e2e-reg-3f2a9c", 2025, ct);

        Assert.Same(record, result);
        await _real.DidNotReceiveWithAnyArgs().GetPriorYearAsync(default, default, default, ct);
    }

    [Theory]
    [InlineData("500027")]
    [InlineData("E2E-3f2a9c")]
    [InlineData("org-e2e-3f2a9c")]
    [InlineData(null)]
    public async Task GetPriorYearAsync_sends_every_other_organisation_to_real_reex(
        string? organisationId
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var record = new PriorYearAccreditationDto { Year = 2024 };
        _real.GetPriorYearAsync(organisationId, "reg-1", 2024, ct).Returns(record);

        var result = await _client.GetPriorYearAsync(organisationId, "reg-1", 2024, ct);

        Assert.Same(record, result);
        await _stub.DidNotReceiveWithAnyArgs().GetPriorYearAsync(default, default, default, ct);
    }

    [Fact]
    public async Task GetNationAsync_sends_an_e2e_organisation_to_the_stub()
    {
        var ct = TestContext.Current.CancellationToken;
        _stub.GetNationAsync("e2e-3f2a9c", "e2e-reg-3f2a9c", ct).Returns(Nation.England);

        var result = await _client.GetNationAsync("e2e-3f2a9c", "e2e-reg-3f2a9c", ct);

        Assert.Equal(Nation.England, result);
        await _real.DidNotReceiveWithAnyArgs().GetNationAsync(default, default, ct);
    }

    [Fact]
    public async Task GetNationAsync_sends_every_other_organisation_to_real_reex()
    {
        var ct = TestContext.Current.CancellationToken;
        _real.GetNationAsync("500027", "reg-1", ct).Returns(Nation.Wales);

        var result = await _client.GetNationAsync("500027", "reg-1", ct);

        Assert.Equal(Nation.Wales, result);
        await _stub.DidNotReceiveWithAnyArgs().GetNationAsync(default, default, ct);
    }
}
