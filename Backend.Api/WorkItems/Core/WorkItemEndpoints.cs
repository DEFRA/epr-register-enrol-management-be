using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.WorkItems.Core;

/// <summary>
/// Framework-level HTTP endpoints for ingesting and listing work items. The
/// envelope (id, type, state, submitted-by, payload) is owned by the
/// framework; type-specific behaviour and routes are added by modules under
/// <c>/work-items/&lt;type-id&gt;/...</c>.
/// </summary>
public static class WorkItemEndpoints
{
    [ExcludeFromCodeCoverage]
    public static IEndpointRouteBuilder MapWorkItemFrameworkEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/work-items").WithTags("WorkItems");

        group.MapPost(string.Empty, Submit)
            .WithName("SubmitWorkItem")
            .DisableValidation()
            .RequireAuthorization();

        group.MapGet("/{id:guid}", GetById)
            .WithName("GetWorkItemById")
            .RequireAuthorization();

        group.MapGet(string.Empty, GetAll)
            .WithName("ListWorkItems")
            .RequireAuthorization();

        return app;
    }

    internal static async Task<Results<CreatedAtRoute<WorkItemResponse>, ProblemHttpResult>> Submit(
        JsonElement body,
        HttpContext httpContext,
        [FromServices] IWorkItemRegistry registry,
        [FromServices] IWorkItemPersistence persistence,
        CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return BadRequest("Invalid request", "Request body must be a JSON object.");
        }

        if (!body.TryGetProperty("typeId", out var typeIdElement) ||
            typeIdElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(typeIdElement.GetString()))
        {
            return BadRequest("Invalid request", "'typeId' is required and must be a non-empty string.");
        }

        var typeId = typeIdElement.GetString()!;
        var type = registry.Find(typeId);
        if (type is null)
        {
            return BadRequest(
                "Unknown work item type",
                $"No work item type is registered with id '{typeId}'.");
        }

        JsonElement? payload = body.TryGetProperty("payload", out var payloadElement) ? payloadElement : null;

        MongoDB.Bson.BsonDocument payloadDocument;
        try
        {
            payloadDocument = WorkItemPayloadConverter.ToBson(payload);
        }
        catch (InvalidWorkItemPayloadException ex)
        {
            return BadRequest("Invalid work item payload", ex.Message);
        }

        var submittedBy = httpContext.User.FindFirstValue("cognito:client_id")
            ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

        var workItem = new WorkItem
        {
            TypeId = type.TypeId,
            StateId = type.InitialState.Id,
            SubmittedBy = submittedBy,
            Payload = payloadDocument
        };

        await persistence.CreateAsync(workItem, cancellationToken);

        var response = ToResponse(workItem);
        return TypedResults.CreatedAtRoute(response, "GetWorkItemById", new { id = workItem.Id });
    }

    private static ProblemHttpResult BadRequest(string title, string detail) =>
        TypedResults.Problem(title: title, detail: detail, statusCode: StatusCodes.Status400BadRequest);

    internal static async Task<Results<Ok<WorkItemResponse>, NotFound>> GetById(
        [FromRoute] Guid id,
        [FromServices] IWorkItemPersistence persistence,
        CancellationToken cancellationToken)
    {
        var workItem = await persistence.GetByIdAsync(id, cancellationToken);
        return workItem is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(workItem));
    }

    internal static async Task<Ok<IReadOnlyCollection<WorkItemResponse>>> GetAll(
        [FromServices] IWorkItemPersistence persistence,
        CancellationToken cancellationToken)
    {
        var items = await persistence.GetAllAsync(cancellationToken);
        IReadOnlyCollection<WorkItemResponse> mapped = items.Select(ToResponse).ToList();
        return TypedResults.Ok(mapped);
    }

    private static WorkItemResponse ToResponse(WorkItem workItem) => new(
        workItem.Id,
        workItem.TypeId,
        workItem.StateId,
        workItem.SubmittedAt,
        workItem.SubmittedBy,
        WorkItemPayloadConverter.ToJson(workItem.Payload));
}
