using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Framework-level HTTP endpoints for ingesting and listing work items. The
/// envelope (id, type, state, submitted-by, payload) is owned by the
/// framework; type-specific behaviour and routes are added by modules under
/// <c>/work-items/&lt;type-id&gt;/...</c>.
/// </summary>
public static class WorkItemEndpoints
{
    /// <summary>
    /// Role that lets a user read every work item regardless of submitter.
    /// Standard callers (organisations / BFFs acting on their behalf) only
    /// see items they themselves submitted; case workers / assessors with
    /// this role see all of them.
    /// </summary>
    public const string CaseWorkerRole = "case-worker";

    // Request body size caps (epr-rvz). The work item endpoints all parse
    // their JSON body manually after .DisableValidation(), so without an
    // explicit cap an attacker can POST arbitrarily large payloads and
    // force in-memory JSON / BSON parsing before any size guard fires.
    // The caps are deliberately generous for the legitimate use cases
    // (a real submission payload is well under 1 MB; a note is well under
    // 100 KB; status / assign carry just a couple of small string fields).
    public const long MaxSubmitBodyBytes = 1 * 1024 * 1024;       // 1 MB
    public const long MaxNoteBodyBytes = 100 * 1024;              // 100 KB
    public const long MaxAssignBodyBytes = 10 * 1024;             // 10 KB
    public const long MaxTaskStatusBodyBytes = 10 * 1024;         // 10 KB

    [ExcludeFromCodeCoverage]
    public static IEndpointRouteBuilder MapWorkItemFrameworkEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/work-items").WithTags("WorkItems");

        group.MapPost(string.Empty, Submit)
            .WithName("SubmitWorkItem")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxSubmitBodyBytes))
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

        group.MapPut("/{id:guid}/tasks/{taskId}/status", SetTaskStatus)
            .WithName("SetWorkItemTaskStatus")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxTaskStatusBodyBytes))
            .RequireAuthorization();

        group.MapPost("/{id:guid}/actions/{actionId}", ApplyAction)
            .WithName("ApplyWorkItemAction")
            .RequireAuthorization();

        group.MapPost("/{id:guid}/assign", Assign)
            .WithName("AssignWorkItem")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxAssignBodyBytes))
            .RequireAuthorization();

        group.MapPost("/{id:guid}/unassign", Unassign)
            .WithName("UnassignWorkItem")
            .RequireAuthorization();

        group.MapPost("/{id:guid}/notes", AddNote)
            .WithName("AddWorkItemNote")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxNoteBodyBytes))
            .RequireAuthorization();

        return app;
    }

    internal static async Task<Results<CreatedAtRoute<WorkItemResponse>, ProblemHttpResult>> Submit(
        JsonElement body,
        HttpContext httpContext,
        [FromServices] IWorkItemRegistry registry,
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

        // Routed through the engine so the framework owns audit-log
        // composition for the birth event in the same place it owns every
        // other state-changing entry. The engine writes the document and
        // its first 'work-item-submitted' audit entry in a single
        // CreateAsync call.
        var result = await engine.SubmitAsync(
            type, payloadDocument, submittedBy, httpContext.User, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.FailureCode switch
            {
                WorkItemActionFailureCode.MissingActorIdentity
                    => TypedResults.Problem(
                        title: "Authentication required",
                        detail: result.Message,
                        statusCode: StatusCodes.Status401Unauthorized),
                _ => TypedResults.Problem(
                    title: "Invalid request",
                    detail: result.Message,
                    statusCode: StatusCodes.Status400BadRequest)
            };
        }

        var workItem = result.WorkItem!;
        var response = ToResponse(engine.Project(workItem));
        return TypedResults.CreatedAtRoute(response, "GetWorkItemById", new { id = workItem.Id });
    }

    private static ProblemHttpResult BadRequest(string title, string detail) =>
        TypedResults.Problem(title: title, detail: detail, statusCode: StatusCodes.Status400BadRequest);

    internal static async Task<Results<Ok<WorkItemResponse>, NotFound>> GetById(
        [FromRoute] Guid id,
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken)
    {
        var workItem = await persistence.GetByIdAsync(id, cancellationToken);
        if (workItem is null || !WorkItemTenancy.CanRead(httpContext.User, workItem))
        {
            // Always return NotFound for cross-tenant access to avoid
            // leaking the existence of items the caller cannot see.
            return TypedResults.NotFound();
        }
        return TypedResults.Ok(ToResponse(engine.Project(workItem)));
    }

    internal static async Task<Results<Ok<WorkItemListResponse>, ProblemHttpResult>> GetAll(
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken)
    {
        var query = WorkItemQueryBinding.FromQueryString(httpContext.Request.Query);

        if (query.ExceedsPageCap)
        {
            return TypedResults.Problem(
                title: "Page out of range",
                detail: $"'page' must be <= {WorkItemQuery.MaxPage}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Tenancy isolation: standard callers only ever see items they
        // themselves submitted. Case workers (with the case-worker role)
        // bypass this filter and see everything.
        if (!httpContext.User.IsInRole(CaseWorkerRole))
        {
            var callerClientId = httpContext.User.FindFirstValue("cognito:client_id")
                ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(callerClientId))
            {
                // No identifiable submitter → no items can belong to the
                // caller, so short-circuit with an empty page rather than
                // ferrying a sentinel into the Mongo filter. Failing
                // closed structurally means a future submitter id can
                // never accidentally collide with the gate.
                return TypedResults.Ok(new WorkItemListResponse(
                    Array.Empty<WorkItemListItemResponse>(),
                    TotalCount: 0,
                    Page: query.NormalisedPage,
                    PageSize: query.NormalisedPageSize));
            }
            query = query with { SubmittedBy = callerClientId };
        }

        var page = await persistence.QueryAsync(query, cancellationToken);

        var items = page.Items
            .Select(w => ToListItemResponse(engine.Project(w)))
            .ToList();

        return TypedResults.Ok(new WorkItemListResponse(items, page.TotalCount, page.Page, page.PageSize));
    }

    /// <summary>
    /// Header name set on a CompleteTask response when the task was already
    /// complete. Lets clients distinguish "first hit" from "replay" without
    /// needing to introspect the audit log.
    /// </summary>
    public const string IdempotentReplayHeader = "X-Idempotent-Replay";

    internal static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> CompleteTask(
        [FromRoute] Guid id,
        [FromRoute] string taskId,
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken)
    {
        if (await EnsureTenantAccessAsync(id, httpContext.User, persistence, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }
        var result = await engine.CompleteTaskAsync(id, taskId, httpContext.User, cancellationToken);
        if (result.IsIdempotentReplay)
        {
            httpContext.Response.Headers[IdempotentReplayHeader] = "true";
        }
        return ToHttpResult(result, engine);
    }

    internal static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> SetTaskStatus(
        [FromRoute] Guid id,
        [FromRoute] string taskId,
        JsonElement body,
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken)
    {
        if (await EnsureTenantAccessAsync(id, httpContext.User, persistence, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }
        if (body.ValueKind != JsonValueKind.Object)
        {
            return BadRequest(
                "Invalid request",
                "Request body must be a JSON object containing 'status'.");
        }
        if (!body.TryGetProperty("status", out var statusElement)
            || statusElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(statusElement.GetString()))
        {
            return BadRequest(
                "Invalid request",
                "'status' is required and must be a non-empty string.");
        }

        // Case-insensitive bind matches the JSON enum convention used
        // elsewhere on the wire and means a UI can send "InProgress" or
        // "in-progress"-style casing without breaking the API.
        if (!Enum.TryParse<WorkItemTaskStatus>(statusElement.GetString(), ignoreCase: true, out var status)
            || !Enum.IsDefined(status))
        {
            return BadRequest(
                "Invalid status",
                $"'{statusElement.GetString()}' is not a recognised task status. " +
                $"Expected one of: {string.Join(", ", Enum.GetNames<WorkItemTaskStatus>())}.");
        }

        var result = await engine.SetTaskStatusAsync(id, taskId, status, httpContext.User, cancellationToken);
        return ToHttpResult(result, engine);
    }

    internal static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> ApplyAction(
        [FromRoute] Guid id,
        [FromRoute] string actionId,
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken)
    {
        if (await EnsureTenantAccessAsync(id, httpContext.User, persistence, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }
        var result = await engine.ApplyActionAsync(id, actionId, httpContext.User, cancellationToken);
        return ToHttpResult(result, engine);
    }

    internal static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> Assign(
        [FromRoute] Guid id,
        JsonElement body,
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken)
    {
        if (await EnsureTenantAccessAsync(id, httpContext.User, persistence, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }
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
        if (result.IsIdempotentReplay)
        {
            httpContext.Response.Headers[IdempotentReplayHeader] = "true";
        }
        return ToHttpResult(result, engine);
    }

    internal static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> Unassign(
        [FromRoute] Guid id,
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken)
    {
        if (await EnsureTenantAccessAsync(id, httpContext.User, persistence, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }
        var result = await engine.UnassignAsync(id, httpContext.User, cancellationToken);
        if (result.IsIdempotentReplay)
        {
            httpContext.Response.Headers[IdempotentReplayHeader] = "true";
        }
        return ToHttpResult(result, engine);
    }

    internal static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> AddNote(
        [FromRoute] Guid id,
        JsonElement body,
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken)
    {
        if (await EnsureTenantAccessAsync(id, httpContext.User, persistence, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }
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

    /// <summary>
    /// Tenancy gate for mutation handlers (epr-0t9). Loads the work item
    /// and verifies the caller may see it via
    /// <see cref="WorkItemTenancy.CanRead"/>; returns the loaded item on
    /// success or <c>null</c> when the caller has no access (in which case
    /// the handler should respond with NotFound to avoid leaking the
    /// existence of cross-tenant items). Without this gate the engine
    /// would happily mutate any item whose id the caller can guess.
    /// </summary>
    private static async Task<WorkItem?> EnsureTenantAccessAsync(
        Guid id,
        ClaimsPrincipal user,
        IWorkItemPersistence persistence,
        CancellationToken cancellationToken)
    {
        var workItem = await persistence.GetByIdAsync(id, cancellationToken);
        if (workItem is null || !WorkItemTenancy.CanRead(user, workItem))
        {
            return null;
        }
        return workItem;
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
            WorkItemActionFailureCode.MissingActorIdentity
                => TypedResults.Problem(
                    title: "Authentication required",
                    detail: result.Message,
                    statusCode: StatusCodes.Status401Unauthorized),
            WorkItemActionFailureCode.IncompleteTasks
                or WorkItemActionFailureCode.TerminalState
                or WorkItemActionFailureCode.ConcurrencyConflict
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
                .ToList(),
            // Audit log (RA-97) is projected in chronological (oldest-first)
            // order so a UI renders a natural top-to-bottom timeline of
            // everything that has happened to the work item. Insertion
            // index is the secondary key so entries written within the
            // same tick (common under FakeTimeProvider, and possible in
            // production when a single engine call appends two entries
            // back-to-back) keep their append order on the wire instead
            // of relying on undefined behaviour from a tied OrderBy
            // (epr-s4y).
            w.AuditLog
                .Select((e, i) => (Entry: e, Index: i))
                .OrderBy(x => x.Entry.CreatedAt)
                .ThenBy(x => x.Index)
                .Select(x => new WorkItemAuditEntryResponse(
                    x.Entry.Id,
                    x.Entry.Action,
                    x.Entry.ActionDisplayName,
                    x.Entry.Details,
                    x.Entry.CreatedAt,
                    x.Entry.CreatedBy,
                    x.Entry.CreatedByName))
                .ToList());
    }

    /// <summary>
    /// Slim per-item projection used by the list endpoint (epr-4pf).
    /// Identical to <see cref="ToResponse(WorkItemEngineProjection)"/>
    /// except the per-item <c>Notes</c> and <c>AuditLog</c> collections
    /// are omitted entirely from the wire shape — they would otherwise
    /// dominate the payload of a 100-row page even though no list view
    /// renders them.
    /// </summary>
    internal static WorkItemListItemResponse ToListItemResponse(WorkItemEngineProjection projection)
    {
        var w = projection.WorkItem;
        return new WorkItemListItemResponse(
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
            w.AssignedBy);
    }
}