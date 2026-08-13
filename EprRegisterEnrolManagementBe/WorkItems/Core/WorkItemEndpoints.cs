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
                    "x-cdp-client-id",
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
            httpContext.User.FindFirstValue("client_id")
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
            result.IsIdempotentReplay ? "Work item submission was an idempotent replay" : "Work item submission succeeded",
            new Dictionary<string, object?>
            {
                ["work_item.id"] = workItem.Id.ToString(),
                ["work_item.type_id"] = typeId,
                ["caller.client_id"] = submittedBy ?? "(unknown)",
            }
        );
        // RA-311/MBE-3: a retried "submit application" call for an
        // operatorApplicationId already on file is handed the existing
        // work item rather than a new one — surface that via the same
        // X-Idempotent-Replay header the other idempotent mutations use so
        // a caller (the operator backend) can tell first-hit from replay.
        if (result.IsIdempotentReplay)
        {
            httpContext.Response.Headers[IdempotentReplayHeader] = "true";
        }
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
        if (workItem is null)
        {
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

        var page = await persistence.QueryAsync(query, cancellationToken);

        var items = page
            .Items.Select(w => ToListItemResponse(engine.Project(w), timeProvider))
            .ToList();

        return TypedResults.Ok(
            new WorkItemListResponse(items, page.TotalCount, page.Page, page.PageSize)
        );
    }

    /// <summary>
    /// Header name set on a response whose operation was a no-op because the
    /// work item was already in the requested condition. Lets clients
    /// distinguish "first hit" from "replay" without needing to introspect the
    /// audit log.
    /// </summary>
    public const string IdempotentReplayHeader = "X-Idempotent-Replay";

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
        // Security boundary (RA-311/MBE-1 review): some transitions
        // (currently the re-accreditation module's resume-during-*/
        // continue-review-during-* pairs) are declared with
        // CallerInvocable: false because several of them share the same
        // FromStateId — the engine's normal "is the item in the right
        // state" guard cannot tell them apart, so a caller who could invoke
        // one directly here would pick the target state themselves instead
        // of the module's bespoke service resolving it server-side from
        // audit history. Checked against the work item's own frozen
        // TemplateSnapshot (the same source ApplyActionAsync itself
        // resolves the transition from) so this rejects using the exact
        // rules the item was submitted under.
        var workItem = await persistence.GetByIdAsync(id, cancellationToken);
        var transition = workItem?.TemplateSnapshot?.Transitions.FirstOrDefault(t =>
            string.Equals(t.ActionId, actionId, StringComparison.OrdinalIgnoreCase));
        if (transition is { CallerInvocable: false })
        {
            return ToHttpResult(
                WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.UnknownAction,
                    $"Action '{actionId}' is not declared by work item type '{workItem!.TypeId}'."),
                engine);
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
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    )
    {
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
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    )
    {
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
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    )
    {
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
            WorkItemActionFailureCode.UnknownAction
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
            WorkItemActionFailureCode.TerminalState
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
                ))
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
            ComputeSlaDueDate(w.SlaClock),
            w.Payload.TryGetValue("applicationReference", out var reference) && reference.IsString
                ? reference.AsString
                : null,
            // RA-410: falls back to the item's own state so the field is
            // always populated, even for a projection built without one.
            projection.OriginStateId ?? w.StateId
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
    /// RA-324 / RA-295: the absolute SLA deadline (<c>slaClock.StartedAt +
    /// TargetDuration</c>) for the Applications card's and the case header's
    /// "Due on:" line, or <c>null</c> when no SLA clock has started. Unlike
    /// <see cref="ComputeSla"/> this needs no "now" — the deadline is a fixed
    /// instant, not a relative countdown. Read straight off the live clock, so
    /// an <see cref="ISlaService"/> extend or override is reflected
    /// immediately.
    /// </summary>
    internal static DateTime? ComputeSlaDueDate(WorkItemSlaClock? clock) =>
        clock is null ? null : clock.StartedAt + clock.TargetDuration;

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
            projection.AvailableActions,
            w.AssignedToId,
            w.AssignedToName,
            w.AssignedAt,
            w.AssignedBy,
            slaRemaining,
            slaState,
            ComputeSlaDueDate(w.SlaClock),
            // RA-410: falls back to the item's own state so the field is
            // always populated, even for a projection built without one.
            projection.OriginStateId ?? w.StateId
        );
    }
}
