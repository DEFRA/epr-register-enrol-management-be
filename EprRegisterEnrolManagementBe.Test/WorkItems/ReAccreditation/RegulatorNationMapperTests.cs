using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

public class RegulatorNationMapperTests
{
    [Theory]
    [InlineData("ea", Nation.England)]
    [InlineData("nrw", Nation.Wales)]
    [InlineData("sepa", Nation.Scotland)]
    [InlineData("niea", Nation.NorthernIreland)]
    [InlineData("EA", Nation.England)]
    [InlineData("Nrw", Nation.Wales)]
    public void TryMap_recognised_code_maps_to_expected_nation(string code, Nation expected)
    {
        var result = RegulatorNationMapper.TryMap(code, out var nation);

        Assert.True(result);
        Assert.Equal(expected, nation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryMap_null_or_blank_code_defaults_to_England_and_returns_true(string? code)
    {
        var result = RegulatorNationMapper.TryMap(code, out var nation);

        Assert.True(result);
        Assert.Equal(Nation.England, nation);
    }

    [Fact]
    public void TryMap_unrecognised_code_defaults_to_England_and_returns_false()
    {
        var result = RegulatorNationMapper.TryMap("not-a-real-regulator", out var nation);

        Assert.False(result);
        Assert.Equal(Nation.England, nation);
    }
}
