using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-492: <see cref="WorkItemReferenceGenerator"/> sends a type that declares
/// an <see cref="IWorkItemType.InYearChangeCode"/> to the IYC generator, and
/// every other type to the AP generator.
/// </summary>
public sealed class WorkItemReferenceGeneratorTests
{
    private readonly IApplicationReferenceGenerator _ap =
        Substitute.For<IApplicationReferenceGenerator>();
    private readonly IInYearChangeReferenceGenerator _iyc =
        Substitute.For<IInYearChangeReferenceGenerator>();

    [Fact]
    public async Task GenerateAsync_sends_a_type_declaring_a_change_code_to_the_IYC_generator()
    {
        var payload = new BsonDocument { ["accreditationYear"] = 2026 };
        _iyc.GenerateAsync("ORS", payload, Arg.Any<CancellationToken>())
            .Returns("IYC-ORS-2026-00001");

        var reference = await new WorkItemReferenceGenerator(_ap, _iyc).GenerateAsync(
            new TestWorkItemType("add-overseas-site", "Add overseas site", inYearChangeCode: "ORS"),
            payload,
            attempt: 2,
            TestContext.Current.CancellationToken
        );

        Assert.Equal("IYC-ORS-2026-00001", reference);
        _ap.DidNotReceive().Generate(Arg.Any<BsonDocument>(), Arg.Any<int>());
    }

    [Fact]
    public async Task GenerateAsync_sends_a_type_without_a_change_code_to_the_AP_generator_with_the_attempt()
    {
        var payload = new BsonDocument();
        _ap.Generate(payload, 3).Returns("AP26EA3");

        // The type id alone never selects IYC: only the declared code does.
        var reference = await new WorkItemReferenceGenerator(_ap, _iyc).GenerateAsync(
            new TestWorkItemType("add-overseas-site", "No change code declared"),
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
    public void InYearChangeCode_defaults_to_null_for_a_type_that_does_not_declare_one()
    {
        IWorkItemType type = new MinimalType();

        Assert.Null(type.InYearChangeCode);
    }

    private sealed class MinimalType : IWorkItemType
    {
        public string TypeId => "minimal";
        public string DisplayName => "Minimal";
        public WorkItemState InitialState { get; } = new("submitted", "Submitted");
        public IReadOnlyCollection<WorkItemState> States => [InitialState];
    }
}
