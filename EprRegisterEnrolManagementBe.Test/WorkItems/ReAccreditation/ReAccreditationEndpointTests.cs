using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Endpoints;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

public class ReAccreditationEndpointTests
{
    private readonly IWorkItemPersistence _persistence = Substitute.For<IWorkItemPersistence>();
    private readonly IReAccreditationDecisionService _decisionService =
        Substitute.For<IReAccreditationDecisionService>();

    [Fact]
    public async Task Recommendation_returns_not_found_when_work_item_missing()
    {
        var id = Guid.NewGuid();
        _persistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var result = await ReAccreditationEndpoints.GetRecommendation(
            id, _persistence, _decisionService, TestContext.Current.CancellationToken);

        Assert.IsType<NotFound>(result.Result);
        _decisionService.DidNotReceiveWithAnyArgs().EvaluateRecommendation(default!);
    }

    [Fact]
    public async Task Recommendation_returns_problem_when_work_item_is_wrong_type()
    {
        var id = Guid.NewGuid();
        _persistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(new WorkItem
        {
            Id = id,
            TypeId = "some-other-type",
            StateId = "submitted"
        });

        var result = await ReAccreditationEndpoints.GetRecommendation(
            id, _persistence, _decisionService, TestContext.Current.CancellationToken);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    [Fact]
    public async Task Recommendation_deserialises_payload_and_returns_decision_service_result()
    {
        var id = Guid.NewGuid();
        var payload = new BsonDocument
        {
            ["organisationName"] = "Acme Recycling Ltd",
            ["registrationNumber"] = "EX-12345",
            ["materialsHandled"] = new BsonArray(["plastic", "paper"]),
            ["previousAccreditationYear"] = 2024,
            ["complianceIssuesReported"] = 1
        };
        _persistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(new WorkItem
        {
            Id = id,
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            Payload = payload
        });

        ReAccreditationPayload? capturedPayload = null;
        _decisionService
            .EvaluateRecommendation(Arg.Do<ReAccreditationPayload>(p => capturedPayload = p))
            .Returns(new ReAccreditationRecommendation(
                ReAccreditationRecommendation.Approve, "Looks good"));

        var result = await ReAccreditationEndpoints.GetRecommendation(
            id, _persistence, _decisionService, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<Ok<ReAccreditationRecommendationResponse>>(result.Result);
        Assert.NotNull(ok.Value);
        Assert.Equal(ReAccreditationRecommendation.Approve, ok.Value!.Recommendation);
        Assert.Equal("Looks good", ok.Value.Rationale);

        Assert.NotNull(capturedPayload);
        Assert.Equal("Acme Recycling Ltd", capturedPayload!.OrganisationName);
        Assert.Equal("EX-12345", capturedPayload.RegistrationNumber);
        Assert.Equal(new[] { "plastic", "paper" }, capturedPayload.MaterialsHandled);
        Assert.Equal(2024, capturedPayload.PreviousAccreditationYear);
        Assert.Equal(1, capturedPayload.ComplianceIssuesReported);
    }

    [Fact]
    public async Task Recommendation_passes_empty_payload_when_work_item_payload_is_empty()
    {
        // Submitting a work item without a payload is allowed by the framework.
        // The decision service still gets called (and will recommend
        // more-info-needed) so the endpoint never silently swallows the call.
        var id = Guid.NewGuid();
        _persistence.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(new WorkItem
        {
            Id = id,
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            Payload = new BsonDocument()
        });
        _decisionService
            .EvaluateRecommendation(Arg.Any<ReAccreditationPayload>())
            .Returns(new ReAccreditationRecommendation(
                ReAccreditationRecommendation.MoreInfoNeeded, "Missing fields"));

        var result = await ReAccreditationEndpoints.GetRecommendation(
            id, _persistence, _decisionService, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<Ok<ReAccreditationRecommendationResponse>>(result.Result);
        Assert.Equal(ReAccreditationRecommendation.MoreInfoNeeded, ok.Value!.Recommendation);
        _decisionService.Received(1).EvaluateRecommendation(Arg.Any<ReAccreditationPayload>());
    }
}
