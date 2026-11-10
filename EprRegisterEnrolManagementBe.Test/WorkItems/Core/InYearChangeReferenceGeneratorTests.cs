using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-492: the in-year change reference format. The Jira comment of 10 Nov 2026
/// fixes <c>IYC-{code}-{year}-{5-digit sequence}</c> with one sequence per change
/// type per year. The year fallback, the 00001 start and widening past 99999 are
/// implementation choices.
/// </summary>
public sealed class InYearChangeReferenceGeneratorTests
{
    private static readonly DateTimeOffset s_now = new(2027, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly IReferenceSequenceStore _store = Substitute.For<IReferenceSequenceStore>();
    private readonly InYearChangeReferenceGenerator _generator;

    public InYearChangeReferenceGeneratorTests()
    {
        _store.NextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1L);
        _generator = new InYearChangeReferenceGenerator(_store, new FakeTimeProvider(s_now));
    }

    [Fact]
    public async Task GenerateAsync_formats_the_agreed_example_reference()
    {
        _store.NextAsync("IYC-ORS-2026", Arg.Any<CancellationToken>()).Returns(21L);

        var reference = await _generator.GenerateAsync(
            "ORS",
            new BsonDocument { ["accreditationYear"] = 2026 },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("IYC-ORS-2026-00021", reference);
    }

    [Fact]
    public async Task GenerateAsync_allocates_from_a_per_change_type_per_year_sequence_key()
    {
        await _generator.GenerateAsync(
            "ORS",
            new BsonDocument { ["accreditationYear"] = 2026 },
            TestContext.Current.CancellationToken
        );
        await _generator.GenerateAsync(
            "SIP",
            new BsonDocument { ["accreditationYear"] = 2026 },
            TestContext.Current.CancellationToken
        );

        await _store.Received(1).NextAsync("IYC-ORS-2026", Arg.Any<CancellationToken>());
        await _store.Received(1).NextAsync("IYC-SIP-2026", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateAsync_uses_the_current_utc_year_when_accreditationYear_is_absent()
    {
        var reference = await _generator.GenerateAsync(
            "ORS",
            new BsonDocument(),
            TestContext.Current.CancellationToken
        );

        Assert.Equal("IYC-ORS-2027-00001", reference);
        await _store.Received(1).NextAsync("IYC-ORS-2027", Arg.Any<CancellationToken>());
    }

    public static TheoryData<BsonValue> UnusableYears =>
        new()
        {
            new BsonString("2026"),
            BsonNull.Value,
            new BsonInt32(26),
            new BsonInt32(20260),
            new BsonInt32(-2026),
            new BsonDouble(2026.5),
            new BsonDouble(double.NaN),
            new BsonInt64(long.MaxValue),
            new BsonDecimal128(Decimal128.MaxValue),
        };

    [Theory]
    [MemberData(nameof(UnusableYears))]
    public async Task GenerateAsync_falls_back_to_the_utc_year_for_an_unusable_accreditationYear(
        BsonValue year
    )
    {
        var reference = await _generator.GenerateAsync(
            "ORS",
            new BsonDocument { ["accreditationYear"] = year },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("IYC-ORS-2027-00001", reference);
    }

    public static TheoryData<BsonValue> NumericYearRepresentations =>
        new() { new BsonInt32(2026), new BsonInt64(2026L), new BsonDouble(2026.0) };

    [Theory]
    [MemberData(nameof(NumericYearRepresentations))]
    public async Task GenerateAsync_accepts_any_whole_numeric_four_digit_accreditationYear(
        BsonValue year
    )
    {
        var reference = await _generator.GenerateAsync(
            "ORS",
            new BsonDocument { ["accreditationYear"] = year },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("IYC-ORS-2026-00001", reference);
    }

    [Theory]
    [InlineData(1L, "IYC-ORS-2026-00001")]
    [InlineData(99999L, "IYC-ORS-2026-99999")]
    [InlineData(100000L, "IYC-ORS-2026-100000")]
    public async Task GenerateAsync_zero_pads_to_five_digits_and_widens_past_99999(
        long sequence,
        string expected
    )
    {
        _store.NextAsync("IYC-ORS-2026", Arg.Any<CancellationToken>()).Returns(sequence);

        var reference = await _generator.GenerateAsync(
            "ORS",
            new BsonDocument { ["accreditationYear"] = 2026 },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(expected, reference);
    }

    [Fact]
    public async Task GenerateAsync_rejects_a_blank_change_type_code_without_allocating()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _generator.GenerateAsync(" ", new BsonDocument(), TestContext.Current.CancellationToken)
        );
        await _store.DidNotReceive().NextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
