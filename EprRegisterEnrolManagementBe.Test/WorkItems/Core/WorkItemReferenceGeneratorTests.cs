using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-492: <see cref="WorkItemReferenceGenerator"/> sends in-year change types
/// to the IYC generator and every other type to the AP generator.
/// </summary>
public sealed class WorkItemReferenceGeneratorTests
{
    private readonly IApplicationReferenceGenerator _ap =
        Substitute.For<IApplicationReferenceGenerator>();
    private readonly IInYearChangeReferenceGenerator _iyc =
        Substitute.For<IInYearChangeReferenceGenerator>();

    [Fact]
    public async Task GenerateAsync_sends_add_overseas_site_to_the_IYC_generator_with_code_ORS()
    {
        var payload = new BsonDocument { ["accreditationYear"] = 2026 };
        _iyc.GenerateAsync("ORS", payload, Arg.Any<CancellationToken>())
            .Returns("IYC-ORS-2026-00001");

        var reference = await new WorkItemReferenceGenerator(_ap, _iyc).GenerateAsync(
            "add-overseas-site",
            payload,
            attempt: 2,
            TestContext.Current.CancellationToken
        );

        Assert.Equal("IYC-ORS-2026-00001", reference);
        _ap.DidNotReceive().Generate(Arg.Any<BsonDocument>(), Arg.Any<int>());
    }

    [Fact]
    public async Task GenerateAsync_sends_other_types_to_the_AP_generator_with_the_attempt()
    {
        var payload = new BsonDocument();
        _ap.Generate(payload, 3).Returns("AP26EA3");

        var reference = await new WorkItemReferenceGenerator(_ap, _iyc).GenerateAsync(
            "re-accreditation",
            payload,
            attempt: 3,
            TestContext.Current.CancellationToken
        );

        Assert.Equal("AP26EA3", reference);
        await _iyc
            .DidNotReceive()
            .GenerateAsync(Arg.Any<string>(), Arg.Any<BsonDocument>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateAsync_throws_instead_of_falling_back_to_AP_when_no_IYC_generator_is_configured()
    {
        var generator = new WorkItemReferenceGenerator(_ap);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            generator.GenerateAsync(
                "add-overseas-site",
                new BsonDocument(),
                attempt: 1,
                TestContext.Current.CancellationToken
            )
        );
        _ap.DidNotReceive().Generate(Arg.Any<BsonDocument>(), Arg.Any<int>());
    }
}
