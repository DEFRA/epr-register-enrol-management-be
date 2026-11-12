using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Driver;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-739: <c>SUB-{yyyy}-{sequence}</c>, one sequence per year, starting at
/// 0001 and growing past 9999 rather than wrapping.
/// </summary>
public sealed class SubmissionReferenceGeneratorTests
{
    private readonly IReferenceSequenceStore _store = Substitute.For<IReferenceSequenceStore>();

    [Theory]
    [InlineData(1L, "SUB-2026-0001")]
    [InlineData(9999L, "SUB-2026-9999")]
    [InlineData(10000L, "SUB-2026-10000")]
    public async Task GenerateAsync_pads_to_four_digits_and_grows_past_9999(long sequence, string expected)
    {
        var session = Substitute.For<IClientSessionHandle>();
        _store.NextAsync(session, "SUB-2026", Arg.Any<CancellationToken>()).Returns(sequence);

        var reference = await new SubmissionReferenceGenerator(_store).GenerateAsync(
            session,
            2026,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(expected, reference);
    }

    [Fact]
    public async Task GenerateAsync_without_a_session_allocates_outside_a_transaction()
    {
        _store.NextAsync("SUB-2027", Arg.Any<CancellationToken>()).Returns(7L);

        var reference = await new SubmissionReferenceGenerator(_store).GenerateAsync(
            session: null,
            2027,
            TestContext.Current.CancellationToken
        );

        Assert.Equal("SUB-2027-0007", reference);
    }
}
