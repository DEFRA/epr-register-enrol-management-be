using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx.Dtos;
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
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
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

    // RA-291: same rationale as MaxRationaleBodyBytes for the query
    // endpoint, which also calls .DisableValidation() and therefore must
    // carry its own explicit size guard. A legitimate body is six short
    // section ids plus a reason capped at 200 words, so 16 KiB is generous
    // while still making a multi-MB body pointless.
    public const long MaxQueryBodyBytes = 16 * 1024;

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
            .RequireAuthorization();

        // Operator-backend endpoint for when payment is confirmed programmatically.
        // Not yet wired to the caseworker UI — caseworkers use the payment-received
        // engine action instead. Reserved for future operator backend integration.
        group.MapPost("/{id:guid}/payment-completed", RecordPaymentCompleted)
            .WithName("RecordReAccreditationPaymentCompleted")
            .RequireAuthorization();

        // RA-132: bespoke approve endpoint. The generic
        // /work-items/{id}/actions/approve transition still exists in the
        // template snapshot, but the module-owned route is the canonical
        // path because it stamps the accreditation id / SLA clock and
        // queues the publishing job; routing through the framework's
        // generic action handler would skip those side effects.
        group.MapPost("/{id:guid}/approve", Approve)
            .WithName("ApproveReAccreditation")
            .RequireAuthorization();

        // RA-291: bespoke query endpoint. The caller never names an action —
        // the service derives the right query-during-* transition from the
        // work item's current state — and the query sections + reason are
        // recorded on the audit log, which the generic action route cannot do.
        group.MapPost("/{id:guid}/query", QueryApplication)
            .WithName("QueryReAccreditation")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxQueryBodyBytes))
            .RequireAuthorization();

        // Live prior-year accreditation data from ReEx, scoped to this
        // work item type because no other module needs ReEx access.
        group.MapGet("/{id:guid}/prior-year", GetPriorYear)
            .WithName("GetReAccreditationPriorYear")
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
        // RA-323: every caseworker holds the same role, so recording the
        // decision rationale (which completes the record-decision-rationale
        // prerequisite task and appends the justification note the
        // approve/reject transition is built upon) is open to any
        // authenticated caseworker.
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

    /// <summary>
    /// Operator-backend endpoint for programmatic payment confirmation.
    /// Stamps the SLA clock from the operator-supplied <c>paidAt</c> timestamp,
    /// transitions directly to <c>assessment-in-progress</c>, and records
    /// four operator-attributed audit entries. Not yet wired to the caseworker
    /// UI — caseworkers use the <c>payment-received</c> engine action instead.
    /// Reserved for future operator backend integration.
    /// </summary>
    public static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> RecordPaymentCompleted(
        [FromRoute] Guid id,
        [FromBody] PaymentCompletedRequest request,
        [FromServices] IReAccreditationPaymentService paymentService,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken)
    {
        var result = await paymentService.RecordPaymentAsync(id, request, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.FailureCode == WorkItemActionFailureCode.WorkItemNotFound
                ? TypedResults.NotFound()
                : TypedResults.Problem(
                    title: "Could not record payment",
                    detail: result.Message,
                    statusCode: StatusCodes.Status400BadRequest);
        }

        return TypedResults.Ok(WorkItemEndpoints.ToResponse(engine.Project(result.WorkItem!)));
    }

    /// <summary>
    /// Return live prior-year accreditation data from ReEx for the given
    /// re-accreditation work item. Uses the ReEx organisation and registration
    /// identifiers stored in the work item payload (populated by the operator
    /// backend at submission time). Returns 404 when the identifiers are absent
    /// (work item created via the case management form) or when ReEx returns no
    /// matching accreditation for the prior year.
    /// </summary>
    private static async Task<Results<Ok<PriorYearAccreditationDto>, NotFound, ProblemHttpResult>> GetPriorYear(
        [FromRoute] Guid id,
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IReExAccreditationClient reExClient,
        CancellationToken cancellationToken)
    {
        var workItem = await persistence.GetByIdAsync(id, cancellationToken);
        if (workItem is null || !WorkItemTenancy.CanRead(httpContext.User, workItem))
            return TypedResults.NotFound();

        if (!string.Equals(workItem.TypeId, ReAccreditationType.Id, StringComparison.OrdinalIgnoreCase))
            return TypedResults.Problem(
                title: "Wrong work item type",
                detail: $"Work item {id} is of type '{workItem.TypeId}', not '{ReAccreditationType.Id}'.",
                statusCode: StatusCodes.Status400BadRequest);

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

        // PreviousAccreditationYear is set by new operator submissions (Year − 1).
        // Older work items only carry AccreditationYear; derive the prior year from that.
        var priorYearValue = payload?.PreviousAccreditationYear
            ?? (payload?.AccreditationYear is int ay ? ay - 1 : (int?)null);

        var priorYear = await reExClient.GetPriorYearAsync(
            payload?.OperatorOrganisationId,
            payload?.OperatorRegistrationId,
            priorYearValue,
            cancellationToken);

        if (priorYear is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(priorYear);
    }

    /// <summary>
    /// RA-132: approve a re-accreditation work item. Delegates to the
    /// module-scoped <see cref="IReAccreditationApprovalService"/> so the
    /// bespoke approval workflow (accreditation id issuance, SLA clock
    /// stop, queued publishing job) runs atomically with the state
    /// transition. Failure codes map onto problem statuses with the same
    /// vocabulary the framework's <c>/actions/{actionId}</c> endpoint
    /// uses.
    /// </summary>
    public static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> Approve(
        [FromRoute] Guid id,
        HttpContext httpContext,
        [FromServices] IReAccreditationApprovalService approvalService,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken)
    {
        var result = await approvalService.ApproveAsync(id, httpContext.User, cancellationToken);
        if (result.IsSuccess)
        {
            return TypedResults.Ok(WorkItemEndpoints.ToResponse(engine.Project(result.WorkItem!)));
        }

        if (result.FailureCode == WorkItemActionFailureCode.WorkItemNotFound)
        {
            return TypedResults.NotFound();
        }

        var status = result.FailureCode switch
        {
            WorkItemActionFailureCode.MissingActorIdentity => StatusCodes.Status401Unauthorized,
            WorkItemActionFailureCode.NotAuthorized => StatusCodes.Status403Forbidden,
            WorkItemActionFailureCode.ConcurrencyConflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };

        return TypedResults.Problem(
            title: "Could not approve re-accreditation",
            detail: result.Message,
            statusCode: status);
    }

    /// <summary>
    /// RA-291: raise a query against a re-accreditation application. The
    /// body names the sections the case worker needs clarification on and
    /// the reason; the <c>query-during-*</c> transition is derived
    /// server-side from the work item's current state, so the caller cannot
    /// choose one that does not apply.
    ///
    /// Validation failures are 400. A state with no query transition —
    /// including an application that is already <c>queried</c> — is 409,
    /// not a 500.
    /// </summary>
    public static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> QueryApplication(
        [FromRoute] Guid id,
        QueryApplicationRequest request,
        HttpContext httpContext,
        [FromServices] IReAccreditationQueryService queryService,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken)
    {
        if (ReAccreditationQueryValidator.Validate(request) is { } validationError)
        {
            return TypedResults.Problem(
                title: "Invalid query",
                detail: validationError,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await queryService.QueryAsync(
            id, request.Sections!, request.Reason!.Trim(), httpContext.User, cancellationToken);

        if (result.IsSuccess)
        {
            return TypedResults.Ok(WorkItemEndpoints.ToResponse(engine.Project(result.WorkItem!)));
        }

        if (result.FailureCode == WorkItemActionFailureCode.WorkItemNotFound)
        {
            return TypedResults.NotFound();
        }

        var status = result.FailureCode switch
        {
            WorkItemActionFailureCode.MissingActorIdentity => StatusCodes.Status401Unauthorized,
            // RA-291 self-assigns the application on query. RA-323 removed the
            // assign-role tier, so AssignAsync can no longer fail with
            // NotAuthorized — there is no 403 to map here. The remaining
            // AssignAsync failures (missing identity, concurrency) are covered
            // by the arms above/below; anything else falls through to 400.
            // The application is not in a state that can be queried (already
            // queried, terminal) or was raced by another writer: a conflict
            // with the current resource state, not a malformed request. The
            // service resolves the query action from the state itself, so the
            // engine's TerminalState / IncompleteTasks codes are unreachable
            // from here — a state with no query transition is rejected as an
            // InvalidTransition before the engine is called.
            WorkItemActionFailureCode.InvalidTransition
                or WorkItemActionFailureCode.ConcurrencyConflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };

        return TypedResults.Problem(
            title: "Could not query re-accreditation",
            detail: result.Message,
            statusCode: status);
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