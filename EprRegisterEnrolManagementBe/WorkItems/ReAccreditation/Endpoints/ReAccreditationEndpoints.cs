using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Endpoints;

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

        group.MapPost("/{id:guid}/decision-rationale", RecordDecisionRationale)
            .WithName("RecordReAccreditationDecisionRationale")
            .DisableValidation()
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
    /// <summary>
    /// Record the decision rationale for a re-accreditation work item.
    /// Persists the rationale as a note (so it is captured in the standard
    /// audit log) and marks the <c>record-decision-rationale</c> task
    /// complete so the work item satisfies
    /// <see cref="WorkItemTransition.RequiresAllTasksComplete"/> on approve
    /// / reject.
    /// </summary>
    public static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> RecordDecisionRationale(
        [FromRoute] Guid id,
        DecisionRationaleRequest request,
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken)
    {
        var rationale = request?.Rationale?.Trim();
        if (string.IsNullOrWhiteSpace(rationale))
        {
            return TypedResults.Problem(
                title: "Invalid rationale",
                detail: "'rationale' is required and must not be whitespace.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (rationale.Length < ReAccreditationEndpointsRationale.MinRationaleLength)
        {
            return TypedResults.Problem(
                title: "Invalid rationale",
                detail: $"'rationale' must be at least {ReAccreditationEndpointsRationale.MinRationaleLength} characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

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

        var noteText = $"[decision-rationale] {rationale}";
        var noteResult = await engine.AddNoteAsync(id, noteText, httpContext.User, cancellationToken);
        if (!noteResult.IsSuccess)
        {
            return TypedResults.Problem(
                title: "Could not record rationale",
                detail: noteResult.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var taskResult = await engine.CompleteTaskAsync(id, "record-decision-rationale", httpContext.User, cancellationToken);
        if (!taskResult.IsSuccess)
        {
            return TypedResults.Problem(
                title: "Could not complete decision-rationale task",
                detail: taskResult.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        return TypedResults.Ok(WorkItemEndpoints.ToResponse(engine.Project(taskResult.WorkItem!)));
    }
}
public sealed record ReAccreditationRecommendationResponse(string Recommendation, string Rationale);

/// <summary>Request body for <see cref="ReAccreditationEndpoints.RecordDecisionRationale"/>.</summary>
public sealed record DecisionRationaleRequest(string Rationale);

public static partial class ReAccreditationEndpointsRationale
{
    /// <summary>
    /// Minimum rationale length. Picked to force assessors to write a real
    /// sentence rather than a one-character placeholder, while still
    /// permitting short "approved — meets all criteria" decisions.
    /// </summary>
    public const int MinRationaleLength = 10;
}
