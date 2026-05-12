using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
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
internal static class ReAccreditationEndpoints
{
    private static readonly JsonSerializerOptions s_payloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Request body cap (epr-e5h) for the manually-parsed
    // RecordDecisionRationale endpoint. Mirrors the framework's epr-rvz
    // pattern in WorkItemEndpoints — every endpoint that calls
    // .DisableValidation() must pair it with an explicit
    // RequestSizeLimitAttribute so an attacker cannot POST a multi-MB
    // body and force JSON parsing before any size guard fires.
    // 16 KiB is comfortably above the legitimate maximum: a rationale is
    // a short justification (assessor-written prose) capped well below
    // the WorkItem note length limit (4000 chars) plus JSON envelope
    // overhead, but small enough to make abuse pointless.
    public const long MaxRationaleBodyBytes = 16 * 1024;

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
            .WithMetadata(new RequestSizeLimitAttribute(MaxRationaleBodyBytes))
            .RequireAuthorization()
            .WithOpenApi(op =>
            {
                if (op.RequestBody?.Content.TryGetValue("application/json", out var mediaType) == true)
                {
                    mediaType.Example = JsonNode.Parse("""
                        {
                            "rationale": "All assessment tasks completed satisfactorily. Organisation demonstrates adequate technical and financial capacity for re-accreditation."
                        }
                        """);
                }
                return op;
            });

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
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IReAccreditationDecisionService decisionService,
        CancellationToken cancellationToken)
    {
        var workItem = await persistence.GetByIdAsync(id, cancellationToken);
        // Cross-tenant gate (epr-946): mirror the framework's GetById
        // contract — callers without case-worker access who target a work
        // item submitted by a different tenant must see 404 (not 200, not
        // "wrong type") so existence is not leaked.
        if (workItem is null || !WorkItemTenancy.CanRead(httpContext.User, workItem))
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
        // Segregation of duties (epr-jdv): recording the decision rationale is
        // part of the decision act itself — it both completes the
        // record-decision-rationale prerequisite task and appends the
        // justification note that the approve/reject transition is built
        // upon. The framework already gates the approve/reject transitions on
        // DecisionMakerRole; gate this endpoint on the same role so a
        // standard assessor cannot prepare a decision-ready work item that
        // only awaits a DecisionMaker rubber-stamp.
        if (httpContext.User?.IsInRole(ReAccreditationType.DecisionMakerRole) != true)
        {
            return TypedResults.Problem(
                title: "Decision-maker role required",
                detail: $"Recording a decision rationale requires the '{ReAccreditationType.DecisionMakerRole}' role.",
                statusCode: StatusCodes.Status403Forbidden);
        }

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
        // Cross-tenant gate (epr-946): without this check any
        // authenticated caller could record decision-rationale notes
        // (and complete the rationale task) on any work item by id.
        if (workItem is null || !WorkItemTenancy.CanRead(httpContext.User, workItem))
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
        // Atomic compound mutation (see IWorkItemService.AddNoteAndCompleteTaskAsync):
        // both the note and the task completion are persisted in a single
        // ReplaceAsync, so a concurrency conflict or other persistence
        // failure cannot leave the work item with an orphan rationale note
        // and an incomplete record-decision-rationale task.
        var result = await engine.AddNoteAndCompleteTaskAsync(
            id, "record-decision-rationale", noteText, httpContext.User, cancellationToken);
        if (!result.IsSuccess)
        {
            return TypedResults.Problem(
                title: "Could not record decision rationale",
                detail: result.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        return TypedResults.Ok(WorkItemEndpoints.ToResponse(engine.Project(result.WorkItem!)));
    }
}
internal sealed record ReAccreditationRecommendationResponse(string Recommendation, string Rationale);

/// <summary>Request body for <see cref="ReAccreditationEndpoints.RecordDecisionRationale"/>.</summary>
internal sealed record DecisionRationaleRequest(string Rationale);

internal static partial class ReAccreditationEndpointsRationale
{
    /// <summary>
    /// Minimum rationale length. Picked to force assessors to write a real
    /// sentence rather than a one-character placeholder, while still
    /// permitting short "approved — meets all criteria" decisions.
    /// </summary>
    public const int MinRationaleLength = 10;
}