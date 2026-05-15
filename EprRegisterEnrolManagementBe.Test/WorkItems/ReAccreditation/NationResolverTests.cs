using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

public class NationResolverTests
{
    private readonly NationResolver _sut = new();

    // ─────────────────────────── Northern Ireland ───────────────────────────

    [Theory]
    [InlineData("BT1 1AA")]
    [InlineData("BT48 0AH")]
    [InlineData("bt1 2bc")]
    public void Resolve_returns_NorthernIreland_for_BT_postcodes(string postcode)
    {
        Assert.Equal(Nation.NorthernIreland, _sut.Resolve(postcode));
    }

    // ────────────────────────────── Scotland ────────────────────────────────

    [Theory]
    [InlineData("EH1 1AA")]
    [InlineData("G1 1AA")]
    [InlineData("AB10 1AA")]
    [InlineData("KY1 1AA")]
    [InlineData("DD1 1AB")]
    [InlineData("DG1 2AB")]
    [InlineData("FK1 1AB")]
    [InlineData("HS1 1AB")]
    [InlineData("IV1 1AB")]
    [InlineData("KA1 1AB")]
    [InlineData("KW1 1AB")]
    [InlineData("ML1 1AB")]
    [InlineData("PA1 1AB")]
    [InlineData("PH1 1AB")]
    [InlineData("TD1 1AB")]
    [InlineData("ZE1 1AB")]
    [InlineData("eh1 1aa")]
    [InlineData("g1 1aa")]
    public void Resolve_returns_Scotland_for_Scottish_postcodes(string postcode)
    {
        Assert.Equal(Nation.Scotland, _sut.Resolve(postcode));
    }

    // ─────────────────────────────── Wales ──────────────────────────────────

    [Theory]
    [InlineData("CF10 1AA")]
    [InlineData("CH1 1AA")]
    [InlineData("LD1 1AA")]
    [InlineData("LL11 1AA")]
    [InlineData("NP1 1AA")]
    [InlineData("SA1 1AA")]
    [InlineData("SY1 1AA")]
    [InlineData("cf10 1aa")]
    [InlineData("ll11 1aa")]
    public void Resolve_returns_Wales_for_Welsh_postcodes(string postcode)
    {
        Assert.Equal(Nation.Wales, _sut.Resolve(postcode));
    }

    // ─────────────────────────────── England ────────────────────────────────

    [Theory]
    [InlineData("SW1A 1AA")]
    [InlineData("EC1A 1BB")]
    [InlineData("M1 1AE")]
    [InlineData("OX1 2JD")]
    [InlineData("BS1 1AA")]
    [InlineData("e1 1aa")]
    public void Resolve_returns_England_for_English_postcodes(string postcode)
    {
        Assert.Equal(Nation.England, _sut.Resolve(postcode));
    }

    // ──────────────────────────── Null / blank ──────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_returns_England_for_null_or_blank_postcode(string? postcode)
    {
        Assert.Equal(Nation.England, _sut.Resolve(postcode));
    }

    // ─────────────────────────── ExtractAreaCode ────────────────────────────

    [Theory]
    [InlineData("BT1 1AA", "BT")]
    [InlineData("G1 1AA", "G")]
    [InlineData("SW1A 1AA", "SW")]
    [InlineData("M1 1AE", "M")]
    [InlineData("  CF10 1AA", "CF")]
    public void ExtractAreaCode_returns_leading_letters(string postcode, string expected)
    {
        Assert.Equal(expected, NationResolver.ExtractAreaCode(postcode));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1SW 1AA")]
    public void ExtractAreaCode_returns_null_for_invalid_input(string? postcode)
    {
        Assert.Null(NationResolver.ExtractAreaCode(postcode));
    }
}
