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

        group.MapPost("/{id:guid}/tasks/{taskId}/complete", CompleteTask)
            .WithName("CompleteWorkItemTask")
            .RequireAuthorization();

        group.MapPost("/{id:guid}/actions/{actionId}", ApplyAction)
            .WithName("ApplyWorkItemAction")
            .RequireAuthorization();

        group.MapPost("/{id:guid}/assign", Assign)
            .WithName("AssignWorkItem")
            .DisableValidation()
            .RequireAuthorization();

        group.MapPost("/{id:guid}/unassign", Unassign)
            .WithName("UnassignWorkItem")
            .RequireAuthorization();

        group.MapPost("/{id:guid}/notes", AddNote)
            .WithName("AddWorkItemNote")
            .DisableValidation()
            .RequireAuthorization();

        return app;
    }

    internal static async Task<Results<CreatedAtRoute<WorkItemResponse>, ProblemHttpResult>> Submit(
        JsonElement body,
        HttpContext httpContext,
        [FromServices] IWorkItemRegistry registry,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
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

        var snapshot = WorkItemTemplateSnapshot.Capture(type);
        var workItem = new WorkItem
        {
            TypeId = type.TypeId,
            StateId = type.InitialState.Id,
            SubmittedBy = submittedBy,
            TemplateSnapshot = snapshot,
            TemplateVersion = snapshot.TemplateVersion,
            Payload = payloadDocument
        };

        await persistence.CreateAsync(workItem, cancellationToken);

        var response = ToResponse(engine.Project(workItem));
        return TypedResults.CreatedAtRoute(response, "GetWorkItemById", new { id = workItem.Id });
    }

    private static ProblemHttpResult BadRequest(string title, string detail) =>
        TypedResults.Problem(title: title, detail: detail, statusCode: StatusCodes.Status400BadRequest);

    internal static async Task<Results<Ok<WorkItemResponse>, NotFound>> GetById(
        [FromRoute] Guid id,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken)
    {
        var workItem = await persistence.GetByIdAsync(id, cancellationToken);
        return workItem is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(ToResponse(engine.Project(workItem)));
    }

    internal static async Task<Ok<WorkItemListResponse>> GetAll(
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken)
    {
        var query = WorkItemQueryBinding.FromQueryString(httpContext.Request.Query);
        var page = await persistence.QueryAsync(query, cancellationToken);

        var items = page.Items
            .Select(w => ToResponse(engine.Project(w)))
            .ToList();

        return TypedResults.Ok(new WorkItemListResponse(items, page.TotalCount, page.Page, page.PageSize));
    }

    internal static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> CompleteTask(
        [FromRoute] Guid id,
        [FromRoute] string taskId,
        HttpContext httpContext,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken)
    {
        var result = await engine.CompleteTaskAsync(id, taskId, httpContext.User, cancellationToken);
        return ToHttpResult(result, engine);
    }

    internal static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> ApplyAction(
        [FromRoute] Guid id,
        [FromRoute] string actionId,
        HttpContext httpContext,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken)
    {
        var result = await engine.ApplyActionAsync(id, actionId, httpContext.User, cancellationToken);
        return ToHttpResult(result, engine);
    }

    internal static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> Assign(
        [FromRoute] Guid id,
        JsonElement body,
        HttpContext httpContext,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return BadRequest("Invalid request", "Request body must be a JSON object containing 'assigneeId'.");
        }

        if (!body.TryGetProperty("assigneeId", out var assigneeIdElement)
            || assigneeIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(assigneeIdElement.GetString()))
        {
            return BadRequest("Invalid request", "'assigneeId' is required and must be a non-empty string.");
        }

        string? assigneeName = null;
        if (body.TryGetProperty("assigneeName", out var assigneeNameElement)
            && assigneeNameElement.ValueKind == JsonValueKind.String)
        {
            assigneeName = assigneeNameElement.GetString();
        }

        var result = await engine.AssignAsync(
            id, assigneeIdElement.GetString()!, assigneeName, httpContext.User, cancellationToken);
        return ToHttpResult(result, engine);
    }

    internal static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> Unassign(
        [FromRoute] Guid id,
        HttpContext httpContext,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken)
    {
        var result = await engine.UnassignAsync(id, httpContext.User, cancellationToken);
        return ToHttpResult(result, engine);
    }

    internal static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> AddNote(
        [FromRoute] Guid id,
        JsonElement body,
        HttpContext httpContext,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return BadRequest("Invalid request", "Request body must be a JSON object containing 'text'.");
        }

        if (!body.TryGetProperty("text", out var textElement)
            || textElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(textElement.GetString()))
        {
            return BadRequest("Invalid request", "'text' is required and must be a non-empty string.");
        }

        var result = await engine.AddNoteAsync(id, textElement.GetString()!, httpContext.User, cancellationToken);
        return ToHttpResult(result, engine);
    }

    private static Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult> ToHttpResult(
        WorkItemActionResult result, IWorkItemService engine)
    {
        if (result.IsSuccess)
        {
            return TypedResults.Ok(ToResponse(engine.Project(result.WorkItem!)));
        }

        return result.FailureCode switch
        {
            WorkItemActionFailureCode.WorkItemNotFound => TypedResults.NotFound(),
            WorkItemActionFailureCode.TaskNotApplicable
                or WorkItemActionFailureCode.UnknownAction
                or WorkItemActionFailureCode.InvalidTransition
                or WorkItemActionFailureCode.InvalidAssignment
                or WorkItemActionFailureCode.InvalidNote
                => TypedResults.Problem(
                    title: "Invalid action",
                    detail: result.Message,
                    statusCode: StatusCodes.Status400BadRequest),
            WorkItemActionFailureCode.NotAuthorized
                => TypedResults.Problem(
                    title: "Not authorised",
                    detail: result.Message,
                    statusCode: StatusCodes.Status403Forbidden),
            WorkItemActionFailureCode.IncompleteTasks
                or WorkItemActionFailureCode.TerminalState
                => TypedResults.Problem(
                    title: "Action not allowed",
                    detail: result.Message,
                    statusCode: StatusCodes.Status409Conflict),
            _ => TypedResults.Problem(detail: result.Message, statusCode: StatusCodes.Status400BadRequest)
        };
    }

    internal static WorkItemResponse ToResponse(WorkItemEngineProjection projection)
    {
        var w = projection.WorkItem;
        return new WorkItemResponse(
            w.Id,
            w.TypeId,
            w.StateId,
            w.SubmittedAt,
            w.LastModifiedAt,
            w.SubmittedBy,
            projection.TemplateVersion,
            WorkItemPayloadConverter.ToJson(w.Payload),
            projection.Tasks,
            projection.AvailableActions,
            w.AssignedToId,
            w.AssignedToName,
            w.AssignedAt,
            w.AssignedBy,
            // Notes are stored append-only but rendered newest-first so the
            // most relevant context is at the top of an assessor's screen.
            w.Notes
                .OrderByDescending(n => n.CreatedAt)
                .Select(n => new WorkItemNoteResponse(n.Id, n.Text, n.CreatedAt, n.CreatedBy, n.CreatedByName))
                .ToList());
    }
}
