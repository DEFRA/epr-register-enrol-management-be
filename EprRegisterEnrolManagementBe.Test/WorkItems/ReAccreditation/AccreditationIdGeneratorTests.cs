using System.Text.RegularExpressions;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-133: unit tests for <see cref="AccreditationIdGenerator"/>. The
/// generator owns format (<c>ACC-{Year}-{Material[:1]}-{ULID8}</c>),
/// material fallback (<c>X</c>) and cross-collection uniqueness via the
/// injected <see cref="IAccreditationIdLookup"/>; the approval service is
/// not exercised here.
/// </summary>
public class AccreditationIdGeneratorTests
{
    private static readonly Regex s_format =
        new("^ACC-[0-9]{4}-[A-Z]-[0-9A-Z]{8}$", RegexOptions.CultureInvariant);

    private static AccreditationIdGenerator Build(IAccreditationIdLookup? lookup = null)
    {
        lookup ??= NeverCollides();
        return new AccreditationIdGenerator(lookup);
    }

    private static IAccreditationIdLookup NeverCollides()
    {
        var lookup = Substitute.For<IAccreditationIdLookup>();
        lookup.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        return lookup;
    }

    [Theory]
    [InlineData("plastic", 'P')]
    [InlineData("Glass", 'G')]
    [InlineData("metal", 'M')]
    public async Task GenerateAsync_uses_uppercase_first_char_of_material(string material, char expected)
    {
        var sut = Build();

        var id = await sut.GenerateAsync(material, 2027, TestContext.Current.CancellationToken);

        Assert.Matches(s_format, id);
        Assert.Equal($"ACC-2027-{expected}-", id[..11]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GenerateAsync_falls_back_to_X_for_missing_material(string? material)
    {
        var sut = Build();

        var id = await sut.GenerateAsync(material, 2030, TestContext.Current.CancellationToken);

        Assert.Matches(s_format, id);
        Assert.StartsWith("ACC-2030-X-", id);
    }

    [Fact]
    public async Task GenerateAsync_uses_supplied_year_as_four_digit_segment()
    {
        var sut = Build();

        var id = await sut.GenerateAsync("paper", 2028, TestContext.Current.CancellationToken);

        Assert.StartsWith("ACC-2028-P-", id);
    }

    [Fact]
    public async Task GenerateAsync_produces_distinct_values_across_many_calls()
    {
        var sut = Build();

        var ids = new HashSet<string>();
        for (var i = 0; i < 1000; i++)
        {
            ids.Add(await sut.GenerateAsync("plastic", 2027, TestContext.Current.CancellationToken));
        }

        // ULID monotonicity within a millisecond + millisecond-level
        // timestamp prefix make accidental collisions vanishingly
        // unlikely; require strict uniqueness here.
        Assert.Equal(1000, ids.Count);
    }

    [Fact]
    public async Task GenerateAsync_retries_on_collision_then_returns_first_unique_id()
    {
        var lookup = Substitute.For<IAccreditationIdLookup>();
        // First two probes collide, third is unique.
        lookup.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true), Task.FromResult(true), Task.FromResult(false));
        var sut = Build(lookup);

        var id = await sut.GenerateAsync("plastic", 2027, TestContext.Current.CancellationToken);

        Assert.Matches(s_format, id);
        await lookup.Received(3).ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateAsync_throws_when_collisions_exceed_max_attempts()
    {
        var lookup = Substitute.For<IAccreditationIdLookup>();
        lookup.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        var sut = Build(lookup);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.GenerateAsync("plastic", 2027, TestContext.Current.CancellationToken));

        await lookup.Received(AccreditationIdGenerator.MaxAttempts)
            .ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
