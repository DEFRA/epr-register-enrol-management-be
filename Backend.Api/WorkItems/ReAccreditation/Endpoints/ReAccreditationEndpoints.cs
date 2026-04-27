using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Backend.Api.WorkItems.Core;
using Backend.Api.WorkItems.ReAccreditation.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.WorkItems.ReAccreditation.Endpoints;

/// <summary>
/// Module-namespaced HTTP endpoints for the re-accreditation type. Mounted
/// under <c>/work-items/re-accreditation/...</c> to stay isolated from other
/// modules and from the framework's generic routes.
/// </summary>
public static class ReAccreditationEndpoints
{
    private static readonly JsonSerializerOptions s_payloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [ExcludeFromCodeCoverage]
    public static IEndpointRouteBuilder MapReAccreditationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/work-items/re-accreditation").WithTags("ReAccreditation");

        group.MapGet("/{id:guid}/recommendation", GetRecommendation)
            .WithName("GetReAccreditationRecommendation")
            .RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Compute and return the decision-service recommendation for a
    /// re-accreditation work item. Demonstrates that a module can deserialise
    /// its own payload shape on top of the framework's generic
    /// <see cref="WorkItem.Payload"/> envelope and call its own service
    /// objects from its own routes — the framework never has to know.
    /// </summary>
    public static async Task<Results<Ok<ReAccreditationRecommendationResponse>, NotFound, ProblemHttpResult>> GetRecommendation(
        [FromRoute] Guid id,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IReAccreditationDecisionService decisionService,
        CancellationToken cancellationToken)
    {
        var workItem = await persistence.GetByIdAsync(id, cancellationToken);
        if (workItem is null)
        {
            return TypedResults.NotFound();
        }

        if (!string.Equals(workItem.TypeId, ReAccreditationType.Id, StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.Problem(
                title: "Wrong work item type",
                detail: $"Work item {id} is of type '{workItem.TypeId}', not '{ReAccreditationType.Id}'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        ReAccreditationPayload? payload;
        try
        {
            var payloadJson = WorkItemPayloadConverter.ToJson(workItem.Payload);
            payload = payloadJson.Deserialize<ReAccreditationPayload>(s_payloadJsonOptions);
        }
        catch (JsonException ex)
        {
            return TypedResults.Problem(
                title: "Invalid re-accreditation payload",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var recommendation = decisionService.EvaluateRecommendation(payload ?? new ReAccreditationPayload());
        return TypedResults.Ok(new ReAccreditationRecommendationResponse(
            recommendation.Outcome, recommendation.Rationale));
    }
}

/// <summary>HTTP-facing shape returned by the recommendation endpoint.</summary>
public sealed record ReAccreditationRecommendationResponse(string Recommendation, string Rationale);
