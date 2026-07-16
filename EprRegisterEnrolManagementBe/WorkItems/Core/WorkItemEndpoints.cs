using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json;
using EprRegisterEnrolManagementBe.Utils.Logging;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

// Marker type for IStructuredLogger category — WorkItemEndpoints is a
// static class and therefore cannot itself be used as a type argument.
internal sealed class WorkItemEndpointsLogger;

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
    public const long MaxSubmitBodyBytes = 1 * 1024 * 1024; // 1 MB
    public const long MaxNoteBodyBytes = 100 * 1024; // 100 KB
    public const long MaxAssignBodyBytes = 10 * 1024; // 10 KB
    public const long MaxTaskStatusBodyBytes = 10 * 1024; // 10 KB

    [ExcludeFromCodeCoverage]
    public static IEndpointRouteBuilder MapWorkItemFrameworkEndpoints(
        this IEndpointRouteBuilder app
    )
    {
        var group = app.MapGroup("/work-items").WithTags("WorkItems");

        group
            .MapPost(string.Empty, Submit)
            .WithName("SubmitWorkItem")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxSubmitBodyBytes))
            .RequireAuthorization();

        group.MapGet("/{id:guid}", GetById).WithName("GetWorkItemById").RequireAuthorization();

        group.MapGet(string.Empty, GetAll).WithName("ListWorkItems").RequireAuthorization();

        group
            .MapPost("/{id:guid}/tasks/{taskId}/complete", CompleteTask)
            .WithName("CompleteWorkItemTask")
            .RequireAuthorization();

        group
            .MapPut("/{id:guid}/tasks/{taskId}/status", SetTaskStatus)
            .WithName("SetWorkItemTaskStatus")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxTaskStatusBodyBytes))
            .RequireAuthorization();

        group
            .MapPost("/{id:guid}/actions/{actionId}", ApplyAction)
            .WithName("ApplyWorkItemAction")
            .RequireAuthorization();

        group
            .MapPost("/{id:guid}/assign", Assign)
            .WithName("AssignWorkItem")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxAssignBodyBytes))
            .RequireAuthorization();

        group
            .MapPost("/{id:guid}/unassign", Unassign)
            .WithName("UnassignWorkItem")
            .RequireAuthorization();

        group
            .MapPost("/{id:guid}/notes", AddNote)
            .WithName("AddWorkItemNote")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxNoteBodyBytes))
            .RequireAuthorization();

        group
            .MapPost("/{id:guid}/tasks/{taskId}/notes", AddTaskNote)
            .WithName("AddWorkItemTaskNote")
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
        [FromServices] IStructuredLogger<WorkItemEndpointsLogger> log,
        CancellationToken cancellationToken
    )
    {
        var req = httpContext.Request;
        var bodyText = body.ValueKind != JsonValueKind.Undefined ? body.GetRawText() : "(empty)";
        // Truncate very large bodies in the log — the 1 MB cap still applies
        // at the framework level; this is just for readability.
        const int MaxLoggedBodyChars = 4096;
        var loggedBody =
            bodyText.Length > MaxLoggedBodyChars
                ? bodyText[..MaxLoggedBodyChars] + $"…(truncated, total {bodyText.Length} chars)"
                : bodyText;

        log.Log(
            LogLevel.Information,
            "Work item submission received",
            new Dictionary<string, object?>
            {
                ["http.request.method"] = req.Method,
                ["url.path"] = req.Path.Value,
                ["http.request.body"] = loggedBody,
                ["caller.client_id"] = req.Headers.TryGetValue(
                    "x-cdp-cognito-client-id",
                    out var cid
                )
                    ? cid.ToString()
                    : "(absent)",
                ["caller.user_id"] = req.Headers.TryGetValue("x-cdp-user-id", out var uid)
                    ? uid.ToString()
                    : "(absent)",
                ["caller.user_name"] = req.Headers.TryGetValue("x-cdp-user-name", out var uname)
                    ? uname.ToString()
                    : "(absent)",
                ["http.request.mime_type"] = req.ContentType ?? "(absent)",
                ["http.request.body.bytes"] = req.ContentLength?.ToString() ?? "(absent)",
            }
        );

        if (body.ValueKind != JsonValueKind.Object)
        {
            log.Log(
                LogLevel.Warning,
                "Work item submission rejected: body is not a JSON object",
                new Dictionary<string, object?>
                {
                    ["error.message"] = $"ValueKind was {body.ValueKind}",
                }
            );
            return BadRequest("Invalid request", "Request body must be a JSON object.");
        }

        if (
            !body.TryGetProperty("typeId", out var typeIdElement)
            || typeIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(typeIdElement.GetString())
        )
        {
            log.Log(
                LogLevel.Warning,
                "Work item submission rejected: missing or empty typeId",
                new Dictionary<string, object?> { ["http.request.body"] = loggedBody }
            );
            return BadRequest(
                "Invalid request",
                "'typeId' is required and must be a non-empty string."
            );
        }

        var typeId = typeIdElement.GetString()!;
        var type = registry.Find(typeId);
        if (type is null)
        {
            log.Log(
                LogLevel.Warning,
                "Work item submission rejected: unknown typeId",
                new Dictionary<string, object?> { ["work_item.type_id"] = typeId }
            );
            return BadRequest(
                "Unknown work item type",
                $"No work item type is registered with id '{typeId}'."
            );
        }

        JsonElement? payload = body.TryGetProperty("payload", out var payloadElement)
            ? payloadElement
            : null;

        MongoDB.Bson.BsonDocument payloadDocument;
        try
        {
            payloadDocument = WorkItemPayloadConverter.ToBson(payload);
        }
        catch (InvalidWorkItemPayloadException ex)
        {
            log.Log(
                LogLevel.Warning,
                "Work item submission rejected: invalid payload",
                new Dictionary<string, object?>
                {
                    ["work_item.type_id"] = typeId,
                    ["error.message"] = ex.Message,
                },
                ex
            );
            return BadRequest("Invalid work item payload", ex.Message);
        }

        var submittedBy =
            httpContext.User.FindFirstValue("cognito:client_id")
            ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

        // RA-126: optional caller-supplied audit context. 'source' is a
        // string when present; reject other JSON types up front so a
        // malformed body cannot silently degrade the audit record.
        //
        // RA-219: 'applicationReference' is NO LONGER accepted from the
        // client. The backend generates it server-side during submission;
        // any value the client puts in the body is ignored (not validated,
        // not passed through) so a caller can never spoof or collide a
        // reference.
        Dictionary<string, string?>? submissionMetadata = null;
        if (body.TryGetProperty("source", out var sourceElement))
        {
            if (sourceElement.ValueKind != JsonValueKind.String)
            {
                log.Log(
                    LogLevel.Warning,
                    "Work item submission rejected: 'source' is not a string",
                    new Dictionary<string, object?>
                    {
                        ["work_item.type_id"] = typeId,
                        ["error.message"] = $"'source' ValueKind was {sourceElement.ValueKind}",
                    }
                );
                return BadRequest("Invalid request body", "'source' must be a string.");
            }
            (submissionMetadata ??= new Dictionary<string, string?>(StringComparer.Ordinal))[
                "source"
            ] = sourceElement.GetString();
        }

        // Routed through the engine so the framework owns audit-log
        // composition for the birth event in the same place it owns every
        // other state-changing entry. The engine writes the document and
        // its first 'work-item-submitted' audit entry in a single
        // CreateAsync call.
        WorkItemActionResult result;
        try
        {
            result = await engine.SubmitAsync(
                type,
                payloadDocument,
                submittedBy,
                httpContext.User,
                submissionMetadata,
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            log.Log(
                LogLevel.Error,
                "Work item submission threw an unhandled exception",
                new Dictionary<string, object?>
                {
                    ["work_item.type_id"] = typeId,
                    ["caller.client_id"] = submittedBy ?? "(unknown)",
                    ["error.type"] = ex.GetType().FullName,
                    ["error.message"] = ex.Message,
                },
                ex
            );
            throw;
        }

        if (!result.IsSuccess)
        {
            log.Log(
                LogLevel.Warning,
                "Work item submission failed",
                new Dictionary<string, object?>
                {
                    ["work_item.type_id"] = typeId,
                    ["caller.client_id"] = submittedBy ?? "(unknown)",
                    ["error.code"] = result.FailureCode.ToString(),
                    ["error.message"] = result.Message,
                }
            );
            return result.FailureCode switch
            {
                WorkItemActionFailureCode.MissingActorIdentity => TypedResults.Problem(
                    title: "Authentication required",
                    detail: result.Message,
                    statusCode: StatusCodes.Status401Unauthorized
                ),
                // RA-219: applicationReference exhaustion is transient and
                // server-side, so surface a clean 503 (retryable) rather than
                // letting the engine throw past this handler as a 500.
                WorkItemActionFailureCode.ApplicationReferenceExhausted => TypedResults.Problem(
                    title: "Submission temporarily unavailable",
                    detail: result.Message,
                    statusCode: StatusCodes.Status503ServiceUnavailable
                ),
                _ => TypedResults.Problem(
                    title: "Invalid request",
                    detail: result.Message,
                    statusCode: StatusCodes.Status400BadRequest
                ),
            };
        }

        var workItem = result.WorkItem!;
        log.Log(
            LogLevel.Information,
            "Work item submission succeeded",
            new Dictionary<string, object?>
            {
                ["work_item.id"] = workItem.Id.ToString(),
                ["work_item.type_id"] = typeId,
                ["caller.client_id"] = submittedBy ?? "(unknown)",
            }
        );
        var response = ToResponse(engine.Project(workItem));
        return TypedResults.CreatedAtRoute(response, "GetWorkItemById", new { id = workItem.Id });
    }

    private static ProblemHttpResult BadRequest(string title, string detail) =>
        TypedResults.Problem(
            title: title,
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest
        );

    internal static async Task<Results<Ok<WorkItemResponse>, NotFound>> GetById(
        [FromRoute] Guid id,
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
        [FromServices] TimeProvider timeProvider,
        CancellationToken cancellationToken
    )
    {
        var workItem = await persistence.GetByIdAsync(id, cancellationToken);
        if (workItem is null || !WorkItemTenancy.CanRead(httpContext.User, workItem))
        {
            // Always return NotFound for cross-tenant access to avoid
            // leaking the existence of items the caller cannot see.
            return TypedResults.NotFound();
        }
        return TypedResults.Ok(ToResponse(engine.Project(workItem), timeProvider));
    }

    internal static async Task<Results<Ok<WorkItemListResponse>, ProblemHttpResult>> GetAll(
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
        [FromServices] TimeProvider timeProvider,
        CancellationToken cancellationToken
    )
    {
        var query = WorkItemQueryBinding.FromQueryString(httpContext.Request.Query);

        if (query.ExceedsPageCap)
        {
            return TypedResults.Problem(
                title: "Page out of range",
                detail: $"'page' must be <= {WorkItemQuery.MaxPage}.",
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        // Tenancy isolation: standard callers only ever see items they
        // themselves submitted. Case workers (with the case-worker role)
        // bypass this filter and see everything.
        if (!httpContext.User.IsInRole(CaseWorkerRole))
        {
            var callerClientId =
                httpContext.User.FindFirstValue("cognito:client_id")
                ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(callerClientId))
            {
                // No identifiable submitter → no items can belong to the
                // caller, so short-circuit with an empty page rather than
                // ferrying a sentinel into the Mongo filter. Failing
                // closed structurally means a future submitter id can
                // never accidentally collide with the gate.
                return TypedResults.Ok(
                    new WorkItemListResponse(
                        Array.Empty<WorkItemListItemResponse>(),
                        TotalCount: 0,
                        Page: query.NormalisedPage,
                        PageSize: query.NormalisedPageSize
                    )
                );
            }
            query = query with { SubmittedBy = callerClientId };
        }

        var page = await persistence.QueryAsync(query, cancellationToken);

        var items = page
            .Items.Select(w => ToListItemResponse(engine.Project(w), timeProvider))
            .ToList();

        return TypedResults.Ok(
            new WorkItemListResponse(items, page.TotalCount, page.Page, page.PageSize)
        );
    }

    /// <summary>
    /// Header name set on a CompleteTask response when the task was already
    /// complete. Lets clients distinguish "first hit" from "replay" without
    /// needing to introspect the audit log.
    /// </summary>
    public const string IdempotentReplayHeader = "X-Idempotent-Replay";

    internal static async Task<
        Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>
    > CompleteTask(
        [FromRoute] Guid id,
        [FromRoute] string taskId,
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    )
    {
        if (
            await EnsureTenantAccessAsync(id, httpContext.User, persistence, cancellationToken)
            is null
        )
        {
            return TypedResults.NotFound();
        }
        var result = await engine.CompleteTaskAsync(
            id,
            taskId,
            httpContext.User,
            cancellationToken
        );
        if (result.IsIdempotentReplay)
        {
            httpContext.Response.Headers[IdempotentReplayHeader] = "true";
        }
        return ToHttpResult(result, engine);
    }

    internal static async Task<
        Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>
    > SetTaskStatus(
        [FromRoute] Guid id,
        [FromRoute] string taskId,
        JsonElement body,
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    )
    {
        if (
            await EnsureTenantAccessAsync(id, httpContext.User, persistence, cancellationToken)
            is null
        )
        {
            return TypedResults.NotFound();
        }
        if (body.ValueKind != JsonValueKind.Object)
        {
            return BadRequest(
                "Invalid request",
                "Request body must be a JSON object containing 'status'."
            );
        }
        if (
            !body.TryGetProperty("status", out var statusElement)
            || statusElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(statusElement.GetString())
        )
        {
            return BadRequest(
                "Invalid request",
                "'status' is required and must be a non-empty string."
            );
        }

        // Case-insensitive bind matches the JSON enum convention used
        // elsewhere on the wire and means a UI can send "InProgress" or
        // "in-progress"-style casing without breaking the API.
        if (
            !Enum.TryParse<WorkItemTaskStatus>(
                statusElement.GetString(),
                ignoreCase: true,
                out var status
            ) || !Enum.IsDefined(status)
        )
        {
            return BadRequest(
                "Invalid status",
                $"'{statusElement.GetString()}' is not a recognised task status. "
                    + $"Expected one of: {string.Join(", ", Enum.GetNames<WorkItemTaskStatus>())}."
            );
        }

        var result = await engine.SetTaskStatusAsync(
            id,
            taskId,
            status,
            httpContext.User,
            cancellationToken
        );
        return ToHttpResult(result, engine);
    }

    internal static async Task<
        Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>
    > ApplyAction(
        [FromRoute] Guid id,
        [FromRoute] string actionId,
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    )
    {
        if (
            await EnsureTenantAccessAsync(id, httpContext.User, persistence, cancellationToken)
            is null
        )
        {
            return TypedResults.NotFound();
        }
        var result = await engine.ApplyActionAsync(
            id,
            actionId,
            httpContext.User,
            cancellationToken
        );
        return ToHttpResult(result, engine);
    }

    internal static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> Assign(
        [FromRoute] Guid id,
        JsonElement body,
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    )
    {
        if (
            await EnsureTenantAccessAsync(id, httpContext.User, persistence, cancellationToken)
            is null
        )
        {
            return TypedResults.NotFound();
        }
        if (body.ValueKind != JsonValueKind.Object)
        {
            return BadRequest(
                "Invalid request",
                "Request body must be a JSON object containing 'assigneeId'."
            );
        }

        if (
            !body.TryGetProperty("assigneeId", out var assigneeIdElement)
            || assigneeIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(assigneeIdElement.GetString())
        )
        {
            return BadRequest(
                "Invalid request",
                "'assigneeId' is required and must be a non-empty string."
            );
        }

        string? assigneeName = null;
        if (
            body.TryGetProperty("assigneeName", out var assigneeNameElement)
            && assigneeNameElement.ValueKind == JsonValueKind.String
        )
        {
            assigneeName = assigneeNameElement.GetString();
        }

        var result = await engine.AssignAsync(
            id,
            assigneeIdElement.GetString()!,
            assigneeName,
            httpContext.User,
            cancellationToken
        );
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
        CancellationToken cancellationToken
    )
    {
        if (
            await EnsureTenantAccessAsync(id, httpContext.User, persistence, cancellationToken)
            is null
        )
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
        CancellationToken cancellationToken
    )
    {
        if (
            await EnsureTenantAccessAsync(id, httpContext.User, persistence, cancellationToken)
            is null
        )
        {
            return TypedResults.NotFound();
        }
        if (body.ValueKind != JsonValueKind.Object)
        {
            return BadRequest(
                "Invalid request",
                "Request body must be a JSON object containing 'text'."
            );
        }

        if (
            !body.TryGetProperty("text", out var textElement)
            || textElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(textElement.GetString())
        )
        {
            return BadRequest(
                "Invalid request",
                "'text' is required and must be a non-empty string."
            );
        }

        var result = await engine.AddNoteAsync(
            id,
            textElement.GetString()!,
            httpContext.User,
            cancellationToken
        );
        return ToHttpResult(result, engine);
    }

    /// <summary>
    /// RA-129 / epr-cky: task-scoped note endpoint. Mirrors
    /// <see cref="AddNote"/>'s body shape, validation and tenancy gate;
    /// the only differences are the task-scoped route and the engine
    /// receiving the <paramref name="taskId"/> so it can validate the
    /// task against the work item's current-state task list and emit a
    /// <c>task-note-added</c> audit entry instead of <c>note-added</c>.
    /// </summary>
    internal static async Task<
        Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>
    > AddTaskNote(
        [FromRoute] Guid id,
        [FromRoute] string taskId,
        JsonElement body,
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    )
    {
        if (
            await EnsureTenantAccessAsync(id, httpContext.User, persistence, cancellationToken)
            is null
        )
        {
            return TypedResults.NotFound();
        }
        if (body.ValueKind != JsonValueKind.Object)
        {
            return BadRequest(
                "Invalid request",
                "Request body must be a JSON object containing 'text'."
            );
        }

        if (
            !body.TryGetProperty("text", out var textElement)
            || textElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(textElement.GetString())
        )
        {
            return BadRequest(
                "Invalid request",
                "'text' is required and must be a non-empty string."
            );
        }

        var result = await engine.AddTaskNoteAsync(
            id,
            taskId,
            textElement.GetString()!,
            httpContext.User,
            cancellationToken
        );
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
        CancellationToken cancellationToken
    )
    {
        var workItem = await persistence.GetByIdAsync(id, cancellationToken);
        if (workItem is null || !WorkItemTenancy.CanRead(user, workItem))
        {
            return null;
        }
        return workItem;
    }

    private static Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult> ToHttpResult(
        WorkItemActionResult result,
        IWorkItemService engine,
        TimeProvider? timeProvider = null
    )
    {
        if (result.IsSuccess)
        {
            return TypedResults.Ok(ToResponse(engine.Project(result.WorkItem!), timeProvider));
        }

        return result.FailureCode switch
        {
            WorkItemActionFailureCode.WorkItemNotFound => TypedResults.NotFound(),
            WorkItemActionFailureCode.TaskNotApplicable
            or WorkItemActionFailureCode.UnknownAction
            or WorkItemActionFailureCode.InvalidTransition
            or WorkItemActionFailureCode.InvalidAssignment
            or WorkItemActionFailureCode.InvalidNote => TypedResults.Problem(
                title: "Invalid action",
                detail: result.Message,
                statusCode: StatusCodes.Status400BadRequest
            ),
            WorkItemActionFailureCode.NotAuthorized => TypedResults.Problem(
                title: "Not authorised",
                detail: result.Message,
                statusCode: StatusCodes.Status403Forbidden
            ),
            WorkItemActionFailureCode.MissingActorIdentity => TypedResults.Problem(
                title: "Authentication required",
                detail: result.Message,
                statusCode: StatusCodes.Status401Unauthorized
            ),
            WorkItemActionFailureCode.IncompleteTasks
            or WorkItemActionFailureCode.TerminalState
            or WorkItemActionFailureCode.ConcurrencyConflict => TypedResults.Problem(
                title: "Action not allowed",
                detail: result.Message,
                statusCode: StatusCodes.Status409Conflict
            ),
            _ => TypedResults.Problem(
                detail: result.Message,
                statusCode: StatusCodes.Status400BadRequest
            ),
        };
    }

    internal static WorkItemResponse ToResponse(
        WorkItemEngineProjection projection,
        TimeProvider? timeProvider = null
    )
    {
        var w = projection.WorkItem;
        var now = timeProvider?.GetUtcNow().UtcDateTime;
        var (slaRemaining, slaState) = ComputeSla(w.SlaClock, now);
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
            w.Notes.OrderByDescending(n => n.CreatedAt)
                .Select(n => new WorkItemNoteResponse(
                    n.Id,
                    n.Text,
                    n.CreatedAt,
                    n.CreatedBy,
                    n.CreatedByName
                )
                {
                    TaskId = n.TaskId,
                })
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
            w.AuditLog.Select((e, i) => (Entry: e, Index: i))
                .OrderBy(x => x.Entry.CreatedAt)
                .ThenBy(x => x.Index)
                .Select(x => new WorkItemAuditEntryResponse(
                    x.Entry.Id,
                    x.Entry.Action,
                    x.Entry.ActionDisplayName,
                    x.Entry.Details,
                    x.Entry.CreatedAt,
                    x.Entry.CreatedBy,
                    x.Entry.CreatedByName
                ))
                .ToList(),
            slaRemaining,
            slaState,
            w.Payload.TryGetValue("applicationReference", out var reference) && reference.IsString
                ? reference.AsString
                : null
        );
    }

    internal static (TimeSpan? Remaining, WorkItemSlaState? State) ComputeSla(
        WorkItemSlaClock? clock,
        DateTime? now
    )
    {
        if (clock is null || now is null)
        {
            return (null, null);
        }
        var remaining = clock.Remaining(now.Value);
        var state = clock.ComputeState(now.Value);
        return (remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, state);
    }

    /// <summary>
    /// Slim per-item projection used by the list endpoint (epr-4pf).
    /// Identical to <see cref="ToResponse(WorkItemEngineProjection)"/>
    /// except the per-item <c>Notes</c> and <c>AuditLog</c> collections
    /// are omitted entirely from the wire shape — they would otherwise
    /// dominate the payload of a 100-row page even though no list view
    /// renders them.
    /// </summary>
    internal static WorkItemListItemResponse ToListItemResponse(
        WorkItemEngineProjection projection,
        TimeProvider? timeProvider = null
    )
    {
        var w = projection.WorkItem;
        var now = timeProvider?.GetUtcNow().UtcDateTime;
        var (slaRemaining, slaState) = ComputeSla(w.SlaClock, now);
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
            w.AssignedBy,
            slaRemaining,
            slaState
        );
    }
}
