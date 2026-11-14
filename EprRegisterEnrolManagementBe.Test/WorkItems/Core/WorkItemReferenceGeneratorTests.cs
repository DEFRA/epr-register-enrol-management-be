using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-492: <see cref="WorkItemReferenceGenerator.ForType"/> chooses the IYC
/// format for a type that declares an <see cref="IWorkItemType.InYearChangeCode"/>
/// and the AP format for every other type.
/// </summary>
public sealed class WorkItemReferenceGeneratorTests
{
    private readonly IApplicationReferenceGenerator _ap =
        Substitute.For<IApplicationReferenceGenerator>();
    private readonly IInYearChangeReferenceGenerator _iyc =
        Substitute.For<IInYearChangeReferenceGenerator>();

    private WorkItemReferenceGenerator Generator => new(_ap, _iyc);

    private static TestWorkItemType InYearChangeType() =>
        new("add-overseas-site", "Add overseas site", inYearChangeCode: "ORS");

    [Fact]
    public async Task ForType_with_a_change_code_generates_through_the_IYC_generator()
    {
        var payload = new BsonDocument { ["accreditationYear"] = 2026 };
        _iyc.GenerateAsync("ORS", payload, Arg.Any<CancellationToken>())
            .Returns("IYC-ORS-2026-00000");

        var reference = await Generator
            .ForType(InYearChangeType(), WorkItemSubmissionOrigin.OperatorBackend)
            .GenerateAsync(payload, attempt: 2, TestContext.Current.CancellationToken);

        Assert.Equal("IYC-ORS-2026-00000", reference);
        _ap.DidNotReceive()
            .Generate(Arg.Any<BsonDocument>(), Arg.Any<WorkItemSubmissionOrigin>(), Arg.Any<int>());
    }

    [Fact]
    public void ForType_with_a_change_code_validates_through_the_IYC_generator()
    {
        var payload = new BsonDocument();
        _iyc.Validate(payload).Returns("bad year");

        Assert.Equal("bad year", Generator
                .ForType(InYearChangeType(), WorkItemSubmissionOrigin.OperatorBackend)
                .ValidatePayload(payload));
    }

    // The submission origin reaches the AP generator unchanged, so only a
    // hand-created work item can use its operatorOrganisationId fallback.
    [Theory]
    [InlineData(WorkItemSubmissionOrigin.OperatorBackend)]
    [InlineData(WorkItemSubmissionOrigin.HandCreated)]
    public async Task ForType_without_a_change_code_uses_the_AP_generator_with_the_origin_and_attempt(
        WorkItemSubmissionOrigin origin
    )
    {
        var payload = new BsonDocument();
        _ap.Generate(payload, origin, 3).Returns("AP26EA3");

        // The type id alone never selects IYC: only the declared code does.
        var format = Generator.ForType(new TestWorkItemType("add-overseas-site", "No code"), origin);

        Assert.Null(format.ValidatePayload(payload));
        Assert.Equal(
            "AP26EA3",
            await format.GenerateAsync(payload, attempt: 3, TestContext.Current.CancellationToken)
        );
        _iyc.DidNotReceive().Validate(Arg.Any<BsonDocument>());
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
