using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-492: the in-year change reference format. The Jira comment of 10 Nov 2026
/// fixes <c>IYC-{code}-{year}-{5-digit sequence}</c> with one sequence per change
/// type per year. RA-492 also decided that the year is mandatory. RA-739 moved
/// the start: each counter starts at 00001 and wraps back to 00001 after 99999.
/// </summary>
public sealed class InYearChangeReferenceGeneratorTests
{
    private readonly IReferenceSequenceStore _store = Substitute.For<IReferenceSequenceStore>();
    private readonly InYearChangeReferenceGenerator _generator;

    public InYearChangeReferenceGeneratorTests()
    {
        _store.NextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1L);
        _generator = new InYearChangeReferenceGenerator(_store);
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
        await _generator.GenerateAsync(
            "ORS",
            new BsonDocument { ["accreditationYear"] = 2027 },
            TestContext.Current.CancellationToken
        );

        await _store.Received(1).NextAsync("IYC-ORS-2026", Arg.Any<CancellationToken>());
        await _store.Received(1).NextAsync("IYC-SIP-2026", Arg.Any<CancellationToken>());
        await _store.Received(1).NextAsync("IYC-ORS-2027", Arg.Any<CancellationToken>());
    }

    // RA-739: the store hands out 1, 2, 3, ... per key. The reference shows
    // 00001 to 99999, then wraps back to 00001; 00000 is never shown.
    [Theory]
    [InlineData(1L, "IYC-ORS-2026-00001")]
    [InlineData(2L, "IYC-ORS-2026-00002")]
    [InlineData(99999L, "IYC-ORS-2026-99999")]
    [InlineData(100000L, "IYC-ORS-2026-00001")]
    [InlineData(100001L, "IYC-ORS-2026-00002")]
    [InlineData(199998L, "IYC-ORS-2026-99999")]
    [InlineData(199999L, "IYC-ORS-2026-00001")]
    public async Task GenerateAsync_starts_at_00001_and_wraps_to_00001_after_99999(
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

    public static TheoryData<BsonValue> NumericYearRepresentations =>
        new() { new BsonInt32(2026), new BsonInt64(2026L), new BsonDouble(2026.0) };

    [Theory]
    [MemberData(nameof(NumericYearRepresentations))]
    public void Validate_accepts_any_whole_numeric_four_digit_year(BsonValue year)
    {
        var payload = new BsonDocument { ["accreditationYear"] = year };

        Assert.Null(_generator.Validate(payload));
        Assert.True(AccreditationYear.TryRead(payload, out var resolved));
        Assert.Equal(2026, resolved);
    }

    public static TheoryData<BsonValue> UnusableYears =>
        new()
        {
            new BsonString("2026"),
            BsonNull.Value,
            new BsonInt32(26),
            new BsonInt32(999),
            new BsonInt32(10000),
            new BsonInt32(-2026),
            new BsonDouble(2026.5),
            new BsonDouble(double.NaN),
            new BsonInt64(long.MaxValue),
            new BsonDecimal128(Decimal128.MaxValue),
        };

    [Theory]
    [MemberData(nameof(UnusableYears))]
    public void Validate_rejects_an_unusable_year(BsonValue year)
    {
        Assert.Equal(
            "payload.accreditationYear must be a 4-digit year for an in-year change.",
            _generator.Validate(new BsonDocument { ["accreditationYear"] = year })
        );
    }

    [Fact]
    public void Validate_rejects_a_payload_without_the_field()
    {
        Assert.NotNull(_generator.Validate(new BsonDocument()));
    }

    [Fact]
    public async Task GenerateAsync_rejects_a_payload_without_a_usable_year_without_allocating()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _generator.GenerateAsync("ORS", new BsonDocument(), TestContext.Current.CancellationToken)
        );
        await _store.DidNotReceive().NextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateAsync_rejects_a_blank_change_type_code_without_allocating()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _generator.GenerateAsync(
                " ",
                new BsonDocument { ["accreditationYear"] = 2026 },
                TestContext.Current.CancellationToken
            )
        );
        await _store.DidNotReceive().NextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
