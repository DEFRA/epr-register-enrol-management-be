using System.Text.RegularExpressions;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-219: unit coverage for the server-side applicationReference generator.
/// Asserts the exact RA-######### wire format and that successive draws vary
/// (so the engine's retry-on-collision loop can actually escape a clash).
/// </summary>
public sealed class ApplicationReferenceGeneratorTests
{
    private static readonly Regex s_format = new(@"^RA-\d{9}$", RegexOptions.Compiled);

    [Fact]
    public void Generate_returns_RA_prefix_and_exactly_nine_digits()
    {
        var generator = new ApplicationReferenceGenerator();

        // Many draws so a value that happens to need zero-padding (e.g. a
        // small number) is exercised — the suffix must always be 9 chars.
        for (var i = 0; i < 1000; i++)
        {
            var reference = generator.Generate();
            Assert.Matches(s_format, reference);
            Assert.StartsWith(ApplicationReferenceGenerator.Prefix, reference);
            Assert.Equal(
                ApplicationReferenceGenerator.Prefix.Length + ApplicationReferenceGenerator.DigitCount,
                reference.Length);
        }
    }

    [Fact]
    public void Generate_produces_varying_values_across_calls()
    {
        var generator = new ApplicationReferenceGenerator();

        var values = new HashSet<string>();
        for (var i = 0; i < 500; i++)
        {
            values.Add(generator.Generate());
        }

        // Over a 10^9 keyspace, 500 cryptographically-random draws are
        // overwhelmingly likely to be distinct; assert strong variation
        // rather than absolute uniqueness to avoid a flaky birthday-paradox
        // failure.
        Assert.True(values.Count > 490, $"expected near-unique draws, got {values.Count} distinct of 500");
    }
}
