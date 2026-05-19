using System.Text.RegularExpressions;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

public class AccreditationIdGeneratorTests
{
    private static readonly Regex s_format = new("^RA-[0-9A-F]{16}$", RegexOptions.CultureInvariant);

    [Fact]
    public void Generate_returns_id_in_RA_hex_format()
    {
        var sut = new AccreditationIdGenerator();

        for (var i = 0; i < 10; i++)
        {
            Assert.Matches(s_format, sut.Generate());
        }
    }

    [Fact]
    public void Generate_produces_distinct_values_across_many_calls()
    {
        var sut = new AccreditationIdGenerator();

        var ids = Enumerable.Range(0, 2000).Select(_ => sut.Generate()).ToHashSet();

        // 64 bits of entropy makes accidental collisions across 2k calls
        // essentially impossible (birthday-bound ~1.1e-13); require strict uniqueness.
        Assert.True(ids.Count == 2000, $"expected 2000 unique ids, got {ids.Count}");
    }
}
