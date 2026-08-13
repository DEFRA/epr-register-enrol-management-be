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
        Converters = { new JsonStringEnumConverter() },
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

    // RA-311/MBE-1: same rationale as MaxQueryBodyBytes, sized larger
    // because a legitimate resume-from-query body additionally carries
    // opaque current-section JSON for up to six sections plus a file
    // reference list, not just section ids and a short reason.
    public const long MaxResumeBodyBytes = 64 * 1024;

    // RA-294/RA-297: same rationale as MaxQueryBodyBytes — a legitimate
    // site-added notification body is a handful of short string fields plus
    // a bool, so 16 KiB is generous while still making a multi-MB body
    // pointless.
    public const long MaxSiteAddedBodyBytes = 16 * 1024;

    // RA-316: same rationale as MaxQueryBodyBytes — the duly-make endpoint calls
    // .DisableValidation() (it owns its own payment-date validation so it can
    // attach a machine-readable errorCode the frontend switches on), so it must
    // carry its own explicit size guard. A legitimate body is a single
    // yyyy-MM-dd string, so 4 KiB is already absurdly generous.
    public const long MaxDulyMakeBodyBytes = 4 * 1024;

    // RA-410: same rationale as MaxDulyMakeBodyBytes — the decision endpoint
    // calls .DisableValidation() (it owns its own outcome validation so it can
    // attach a machine-readable errorCode the frontend switches on), so it must
    // carry its own explicit size guard. A legitimate body is a single
    // "approved"/"rejected" string, so 4 KiB is already absurdly generous.
    public const long MaxDecisionBodyBytes = 4 * 1024;

    /// <summary>
    /// ProblemDetails title for every log-decision failure. Constant across all
    /// of them on purpose: the frontend switches on <c>errorCode</c> or the
    /// status code, never on the title or the human-readable detail.
    /// </summary>
    public const string LogDecisionProblemTitle = "Could not record re-accreditation decision";

    /// <summary>
    /// ProblemDetails title for every duly-make failure. Constant across all of
    /// them on purpose: the frontend switches on <c>errorCode</c>, never on the
    /// title or the human-readable detail.
    /// </summary>
    public const string DulyMakeProblemTitle = "Could not complete duly making";

    [ExcludeFromCodeCoverage]
    public static IEndpointRouteBuilder MapReAccreditationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/work-items/re-accreditation").WithTags("ReAccreditation");

        group
            .MapGet("/{id:guid}/recommendation", GetRecommendation)
            .WithName("GetReAccreditationRecommendation")
            .RequireAuthorization();

        group
            .MapPost("/{id:guid}/decision-rationale", RecordDecisionRationale)
            .WithName("RecordReAccreditationDecisionRationale")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxRationaleBodyBytes))
            .RequireAuthorization();

        // RA-316: bespoke duly-make endpoint. The duly-make transition is
        // registered CallerInvocable: false precisely so this is the only way
        // in — routing through the framework's generic action handler would
        // move the item to duly-made with no payment date and therefore no SLA
        // clock, silently defeating the 12-week SLA the regulator is measured
        // against.
        group
            .MapPost("/{id:guid}/duly-make", DulyMake)
            .WithName("DulyMakeReAccreditation")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxDulyMakeBodyBytes))
            .RequireAuthorization();

        // RA-132: bespoke approve endpoint. The generic
        // /work-items/{id}/actions/approve transition still exists in the
        // template snapshot, but the module-owned route is the canonical
        // path because it stamps the accreditation id / SLA clock and
        // queues the publishing job; routing through the framework's
        // generic action handler would skip those side effects.
        group
            .MapPost("/{id:guid}/approve", Approve)
            .WithName("ApproveReAccreditation")
            .RequireAuthorization();

        // RA-410: bespoke log-decision endpoint. Both hops of a determination
        // (submit-for-decision, then approve/reject) run server-side on this
        // one call — submit-for-decision and reject are registered
        // CallerInvocable: false precisely so this is the only way in. Two
        // calls from the frontend would leave a window in which a failure
        // between them stranded the application in 'awaiting-decision' with no
        // call to action to discharge it.
        group
            .MapPost("/{id:guid}/decision", LogDecision)
            .WithName("LogReAccreditationDecision")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxDecisionBodyBytes))
            .RequireAuthorization();

        // RA-291: bespoke query endpoint. The caller never names an action —
        // the service derives the right query-during-* transition from the
        // work item's current state — and the query sections + reason are
        // recorded on the audit log, which the generic action route cannot do.
        group
            .MapPost("/{id:guid}/query", QueryApplication)
            .WithName("QueryReAccreditation")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxQueryBodyBytes))
            .RequireAuthorization();

        // RA-311/MBE-1: called by the operator backend once an operator has
        // resubmitted a queried application. Like /query, the caller never
        // names an action — the correct resume-during-* transition is
        // resolved server-side from the work item's own query audit
        // history — and the resubmitted section values / file references
        // are recorded on the audit log.
        group
            .MapPost("/{id:guid}/resume-from-query", ResumeFromQuery)
            .WithName("ResumeReAccreditationFromQuery")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxResumeBodyBytes))
            .RequireAuthorization();

        // RA-337: caseworker-facing endpoint that moves a work item on from
        // 'updated' once the resubmission has been reviewed. No body — the
        // correct continue-review-during-* transition is resolved server-side
        // from the work item's own resume-during-* audit history.
        group
            .MapPost("/{id:guid}/continue-review", ContinueReview)
            .WithName("ContinueReAccreditationReview")
            .RequireAuthorization();

        // RA-252: called by the operator backend when an operator withdraws
        // their application. Like /query, the caller never names an action —
        // the correct withdraw/withdraw-during-* transition is resolved
        // server-side from the work item's current state, since the operator
        // backend does not track case-working's finer-grained state machine —
        // and the reason is recorded as a work item note before the
        // transition so the Withdrawn notification email can include it.
        group
            .MapPost("/{id:guid}/withdraw", WithdrawApplication)
            .WithName("WithdrawReAccreditation")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxQueryBodyBytes))
            .RequireAuthorization();

        // Live prior-year accreditation data from ReEx, scoped to this
        // work item type because no other module needs ReEx access.
        group
            .MapGet("/{id:guid}/prior-year", GetPriorYear)
            .WithName("GetReAccreditationPriorYear")
            .RequireAuthorization();

        // RA-294/RA-297: operator-backend notification whenever a new ORS or
        // interim site is added to an accreditation application. There is no
        // state transition — adding a site does not move the application's
        // lifecycle on — so the only side effect is a 'site-added' audit
        // entry. This repo never models ORS/interim-site detail itself (see
        // WorkItem.Payload); the request's fields are recorded verbatim.
        group
            .MapPost("/{id:guid}/site-added", SiteAdded)
            .WithName("ReAccreditationSiteAdded")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxSiteAddedBodyBytes))
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
    public static async Task<
        Results<Ok<ReAccreditationRecommendationResponse>, NotFound, ProblemHttpResult>
    > GetRecommendation(
        [FromRoute] Guid id,
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IReAccreditationDecisionService decisionService,
        CancellationToken cancellationToken
    )
    {
        var workItem = await persistence.GetByIdAsync(id, cancellationToken);
        if (workItem is null)
        {
            return TypedResults.NotFound();
        }

        if (
            !string.Equals(
                workItem.TypeId,
                ReAccreditationType.Id,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return TypedResults.Problem(
                title: "Wrong work item type",
                detail: $"Work item {id} is of type '{workItem.TypeId}', not '{ReAccreditationType.Id}'.",
                statusCode: StatusCodes.Status400BadRequest
            );
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
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var recommendation = decisionService.EvaluateRecommendation(
            payload ?? new ReAccreditationPayload()
        );
        return TypedResults.Ok(
            new ReAccreditationRecommendationResponse(
                recommendation.Outcome,
                recommendation.Rationale
            )
        );
    }

    /// <summary>
    /// Record the decision rationale for a re-accreditation work item,
    /// persisting it as a note so it is captured in the standard audit log.
    ///
    /// RA-410: this used to also tick the <c>record-decision-rationale</c>
    /// task, which gated approve/reject. The gate is gone with the rest of the
    /// task framework, so the endpoint is now purely a note write. It is kept
    /// — rather than folded into the decision endpoint — because a caseworker
    /// may record a rationale at any point during assessment, independently of
    /// reaching a determination.
    /// </summary>
    public static async Task<
        Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>
    > RecordDecisionRationale(
        [FromRoute] Guid id,
        DecisionRationaleRequest request,
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    )
    {
        // RA-323: every caseworker holds the same role, so recording the
        // decision rationale is open to any authenticated caseworker.
        var rationale = request?.Rationale?.Trim();
        if (string.IsNullOrWhiteSpace(rationale))
        {
            return TypedResults.Problem(
                title: "Invalid rationale",
                detail: "'rationale' is required and must not be whitespace.",
                statusCode: StatusCodes.Status400BadRequest
            );
        }
        if (rationale.Length < ReAccreditationEndpointsRationale.MinRationaleLength)
        {
            return TypedResults.Problem(
                title: "Invalid rationale",
                detail: $"'rationale' must be at least {ReAccreditationEndpointsRationale.MinRationaleLength} characters.",
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var workItem = await persistence.GetByIdAsync(id, cancellationToken);
        if (workItem is null)
        {
            return TypedResults.NotFound();
        }
        if (
            !string.Equals(
                workItem.TypeId,
                ReAccreditationType.Id,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return TypedResults.Problem(
                title: "Wrong work item type",
                detail: $"Work item {id} is of type '{workItem.TypeId}', not '{ReAccreditationType.Id}'.",
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var noteText = $"[decision-rationale] {rationale}";
        var result = await engine.AddNoteAsync(id, noteText, httpContext.User, cancellationToken);
        if (!result.IsSuccess)
        {
            return TypedResults.Problem(
                title: "Could not record decision rationale",
                detail: result.Message,
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        return TypedResults.Ok(WorkItemEndpoints.ToResponse(engine.Project(result.WorkItem!)));
    }

    /// <summary>
    /// RA-316: complete duly making for a re-accreditation work item.
    ///
    /// Delegates to the module-scoped <see cref="IReAccreditationDulyMakingService"/>
    /// so the bespoke workflow — anchoring the 12-week SLA clock to the entered
    /// payment date, stamping that date on the payload, notifying the operator
    /// and pushing the new status — runs atomically with the state transition.
    ///
    /// Payment-date validation happens HERE rather than in the service, because
    /// only the endpoint can shape the response the case management frontend
    /// needs: a 400 ProblemDetails carrying a stable machine-readable
    /// <c>errorCode</c> and <c>field</c>, which it binds to a GOV.UK error
    /// summary against the date input. Everything else is a page-level failure
    /// on that side, so the two must stay clearly distinguishable — see
    /// <see cref="ReAccreditationDulyMakingValidator"/> for the code vocabulary,
    /// which is part of the wire contract with management-fe and mgmt-tests.
    /// </summary>
    public static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> DulyMake(
        [FromRoute] Guid id,
        HttpContext httpContext,
        [FromBody] DulyMakeRequest? request,
        [FromServices] IReAccreditationDulyMakingService dulyMakingService,
        [FromServices] IWorkItemService engine,
        [FromServices] TimeProvider timeProvider,
        CancellationToken cancellationToken
    )
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var validation = ReAccreditationDulyMakingValidator.Validate(request?.PaymentDate, today);

        if (!validation.IsValid)
        {
            return TypedResults.Problem(
                title: DulyMakeProblemTitle,
                detail: validation.Detail,
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?>
                {
                    ["errorCode"] = validation.ErrorCode,
                    ["field"] = ReAccreditationDulyMakingValidator.Field,
                }
            );
        }

        var result = await dulyMakingService.CompleteDulyMakingAsync(
            id,
            validation.PaymentDate!.Value,
            httpContext.User,
            cancellationToken
        );

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
            // All three are conflicts with the resource's current state rather
            // than malformed requests: the frontend's correct response is "this
            // application has changed, reload it", never a field-level error.
            WorkItemActionFailureCode.InvalidTransition
            or WorkItemActionFailureCode.TerminalState
            or WorkItemActionFailureCode.ConcurrencyConflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };

        // The only 400 the service itself can produce is a type mismatch. Give
        // it an errorCode too so the frontend never has to parse prose to work
        // out whether a 400 belongs against the date input.
        var extensions =
            result.FailureCode == WorkItemActionFailureCode.UnknownAction
                ? new Dictionary<string, object?> { ["errorCode"] = "wrong-work-item-type" }
                : null;

        return TypedResults.Problem(
            title: DulyMakeProblemTitle,
            detail: result.Message,
            statusCode: status,
            extensions: extensions
        );
    }

    /// <summary>
    /// Return live prior-year accreditation data from ReEx for the given
    /// re-accreditation work item. Uses the ReEx organisation and registration
    /// identifiers stored in the work item payload (populated by the operator
    /// backend at submission time). Returns 404 when the identifiers are absent
    /// (work item created via the case management form) or when ReEx returns no
    /// matching accreditation for the prior year.
    /// </summary>
    private static async Task<
        Results<Ok<PriorYearAccreditationDto>, NotFound, ProblemHttpResult>
    > GetPriorYear(
        [FromRoute] Guid id,
        HttpContext httpContext,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] IReExAccreditationClient reExClient,
        CancellationToken cancellationToken
    )
    {
        var workItem = await persistence.GetByIdAsync(id, cancellationToken);
        if (workItem is null)
            return TypedResults.NotFound();

        if (
            !string.Equals(
                workItem.TypeId,
                ReAccreditationType.Id,
                StringComparison.OrdinalIgnoreCase
            )
        )
            return TypedResults.Problem(
                title: "Wrong work item type",
                detail: $"Work item {id} is of type '{workItem.TypeId}', not '{ReAccreditationType.Id}'.",
                statusCode: StatusCodes.Status400BadRequest
            );

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
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        // PreviousAccreditationYear is set by new operator submissions (Year − 1).
        // Older work items only carry AccreditationYear; derive the prior year from that.
        var priorYearValue =
            payload?.PreviousAccreditationYear
            ?? (payload?.AccreditationYear is int ay ? ay - 1 : (int?)null);

        var priorYear = await reExClient.GetPriorYearAsync(
            payload?.OperatorOrganisationId,
            payload?.OperatorRegistrationId,
            priorYearValue,
            cancellationToken
        );

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
        CancellationToken cancellationToken
    )
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
            WorkItemActionFailureCode.TerminalState
            or WorkItemActionFailureCode.ConcurrencyConflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };

        return TypedResults.Problem(
            title: "Could not approve re-accreditation",
            detail: result.Message,
            statusCode: status
        );
    }

    /// <summary>
    /// RA-410: record the final determination on a re-accreditation
    /// application. One call carries it from <c>assessment-in-progress</c>
    /// through <c>awaiting-decision</c> to <c>approved</c> / <c>rejected</c>,
    /// with both hops applied server-side by
    /// <see cref="IReAccreditationLogDecisionService"/>.
    ///
    /// The body names an outcome, never a state or an action id, so a caller
    /// cannot pick a destination the workflow does not allow. Validation
    /// failures carry a machine-readable <c>errorCode</c> and <c>field</c>
    /// (mirroring <see cref="DulyMake"/>) so the frontend can bind them to a
    /// GOV.UK error summary against the radio group rather than parsing prose.
    ///
    /// A repeat call once the application is already in the requested terminal
    /// state succeeds as an idempotent replay, so a double-click or a retried
    /// request does not fail the caller. An application already closed the
    /// OTHER way is a 409, not a replay: reporting success would tell a
    /// caseworker their refusal landed on an application that is in fact
    /// approved and carrying an issued accreditation id.
    ///
    /// epr-p86e / RA-410: the decision is gated on the operator-journey status
    /// push, fired once before anything is persisted. If it cannot be delivered
    /// within its retry budget the decision is abandoned with a generic 500 and
    /// no state changes — the item stays exactly where it was.
    /// </summary>
    public static async Task<
        Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>
    > LogDecision(
        [FromRoute] Guid id,
        [FromBody] LogDecisionRequest? request,
        HttpContext httpContext,
        [FromServices] IReAccreditationLogDecisionService logDecisionService,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    )
    {
        if (!TryParseOutcome(request?.Outcome, out var outcome))
        {
            return TypedResults.Problem(
                title: LogDecisionProblemTitle,
                detail: "'outcome' is required and must be either 'approved' or 'rejected'.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?>
                {
                    ["errorCode"] = "invalid-outcome",
                    ["field"] = "outcome",
                }
            );
        }

        var result = await logDecisionService.LogDecisionAsync(
            id,
            outcome,
            httpContext.User,
            cancellationToken
        );

        if (result.IsIdempotentReplay)
        {
            httpContext.Response.Headers[WorkItemEndpoints.IdempotentReplayHeader] = "true";
        }

        if (result.IsSuccess)
        {
            return TypedResults.Ok(WorkItemEndpoints.ToResponse(engine.Project(result.WorkItem!)));
        }

        if (result.FailureCode == WorkItemActionFailureCode.WorkItemNotFound)
        {
            return TypedResults.NotFound();
        }

        var decisionStatus = result.FailureCode switch
        {
            WorkItemActionFailureCode.MissingActorIdentity => StatusCodes.Status401Unauthorized,
            WorkItemActionFailureCode.NotAuthorized => StatusCodes.Status403Forbidden,
            // All three are conflicts with the resource's current state rather
            // than malformed requests: the frontend's correct response is "this
            // application has changed, reload it", never a field-level error.
            WorkItemActionFailureCode.InvalidTransition
            or WorkItemActionFailureCode.TerminalState
            or WorkItemActionFailureCode.ConcurrencyConflict => StatusCodes.Status409Conflict,
            // epr-p86e / RA-410: the operator journey could not be notified, so
            // the decision was abandoned before anything was persisted. The
            // request itself was well-formed — this is a server-side dependency
            // being unreachable — so it maps to a generic 500, not a 4xx. No
            // errorCode is attached: the frontend shows a generic try-again.
            WorkItemActionFailureCode.UpstreamNotificationFailed => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status400BadRequest,
        };

        // The only 400 the service itself can produce is a type mismatch. Give
        // it an errorCode too so the frontend never has to parse prose to work
        // out whether a 400 belongs against the outcome input.
        var decisionExtensions =
            result.FailureCode == WorkItemActionFailureCode.UnknownAction
                ? new Dictionary<string, object?> { ["errorCode"] = "wrong-work-item-type" }
                : null;

        return TypedResults.Problem(
            title: LogDecisionProblemTitle,
            detail: result.Message,
            statusCode: decisionStatus,
            extensions: decisionExtensions
        );
    }

    /// <summary>
    /// Bind the wire <c>outcome</c> string onto
    /// <see cref="ReAccreditationDecisionOutcome"/>. Case-insensitive, matching
    /// the JSON enum convention used elsewhere on the wire, but strictly
    /// two-valued: <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>
    /// alone would also accept "0"/"1" and any future member, which would let a
    /// caller reach an outcome the frontend never offers.
    /// </summary>
    private static bool TryParseOutcome(string? value, out ReAccreditationDecisionOutcome outcome)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "approved":
                outcome = ReAccreditationDecisionOutcome.Approved;
                return true;
            case "rejected":
                outcome = ReAccreditationDecisionOutcome.Rejected;
                return true;
            default:
                outcome = default;
                return false;
        }
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
    public static async Task<
        Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>
    > QueryApplication(
        [FromRoute] Guid id,
        QueryApplicationRequest request,
        HttpContext httpContext,
        [FromServices] IReAccreditationQueryService queryService,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    )
    {
        if (ReAccreditationQueryValidator.Validate(request) is { } validationError)
        {
            return TypedResults.Problem(
                title: "Invalid query",
                detail: validationError,
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var result = await queryService.QueryAsync(
            id,
            request.Sections!,
            request.Reason!.Trim(),
            httpContext.User,
            cancellationToken
        );

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
            _ => StatusCodes.Status400BadRequest,
        };

        return TypedResults.Problem(
            title: "Could not query re-accreditation",
            detail: result.Message,
            statusCode: status
        );
    }

    /// <summary>
    /// RA-252: withdraw a re-accreditation application on the operator's
    /// behalf. The body carries the operator's withdrawal reason; the
    /// withdraw/withdraw-during-* transition is derived server-side from the
    /// work item's current state, so the caller cannot choose one that does
    /// not apply.
    ///
    /// Validation failures are 400. A state with no withdraw transition
    /// (already decided) is 409, not a 500. An already-withdrawn work item
    /// succeeds as an idempotent replay, so a duplicate withdraw call does
    /// not fail the caller's retry.
    /// </summary>
    public static async Task<
        Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>
    > WithdrawApplication(
        [FromRoute] Guid id,
        WithdrawApplicationRequest request,
        HttpContext httpContext,
        [FromServices] IReAccreditationWithdrawService withdrawService,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    )
    {
        if (ReAccreditationWithdrawValidator.Validate(request) is { } validationError)
        {
            return TypedResults.Problem(
                title: "Invalid withdrawal",
                detail: validationError,
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var result = await withdrawService.WithdrawAsync(
            id,
            request.Reason!.Trim(),
            httpContext.User,
            cancellationToken
        );

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
            WorkItemActionFailureCode.InvalidTransition
            or WorkItemActionFailureCode.ConcurrencyConflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };

        return TypedResults.Problem(
            title: "Could not withdraw re-accreditation",
            detail: result.Message,
            statusCode: status
        );
    }

    /// <summary>
    /// RA-311/MBE-1: resume a queried re-accreditation application once the
    /// operator backend confirms a resubmission. The body carries the
    /// responder's contact details, the sections addressed, their current
    /// (opaque) values, and file references; the correct
    /// <c>resume-during-*</c> transition is derived server-side from the
    /// work item's own query audit history.
    ///
    /// Validation failures are 400. A state that cannot be resumed from
    /// (never queried, or a decided/withdrawn outcome) is 409, not a 500. A
    /// work item that has already left <c>queried</c> into a valid resume
    /// target succeeds as an idempotent replay, so a duplicate resubmit call
    /// does not fail the caller's retry.
    /// </summary>
    public static async Task<
        Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>
    > ResumeFromQuery(
        [FromRoute] Guid id,
        ResumeFromQueryRequest request,
        HttpContext httpContext,
        [FromServices] IReAccreditationResumeService resumeService,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    )
    {
        if (ReAccreditationResumeValidator.Validate(request) is { } validationError)
        {
            return TypedResults.Problem(
                title: "Invalid resume-from-query request",
                detail: validationError,
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var result = await resumeService.ResumeFromQueryAsync(
            id,
            request,
            httpContext.User,
            cancellationToken
        );

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
            WorkItemActionFailureCode.InvalidTransition
            or WorkItemActionFailureCode.ConcurrencyConflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };

        return TypedResults.Problem(
            title: "Could not resume re-accreditation from query",
            detail: result.Message,
            statusCode: status
        );
    }

    /// <summary>
    /// RA-337: move a re-accreditation work item on from the non-terminal
    /// <c>updated</c> state once a caseworker has reviewed a query
    /// resubmission. No body — the correct <c>continue-review-during-*</c>
    /// transition is derived server-side from the work item's own
    /// <c>resume-during-*</c> audit history.
    ///
    /// A state that cannot be continued from (never resumed into, or a
    /// decided/withdrawn/still-queried outcome) is 409, not a 500. A work
    /// item that has already left <c>updated</c> into a valid continue
    /// target succeeds as an idempotent replay, so a duplicate call does not
    /// fail the caller's retry.
    /// </summary>
    public static async Task<
        Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>
    > ContinueReview(
        [FromRoute] Guid id,
        HttpContext httpContext,
        [FromServices] IReAccreditationContinueReviewService continueReviewService,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    )
    {
        var result = await continueReviewService.ContinueReviewAsync(
            id,
            httpContext.User,
            cancellationToken
        );

        if (result.IsIdempotentReplay)
        {
            httpContext.Response.Headers[WorkItemEndpoints.IdempotentReplayHeader] = "true";
        }

        if (result.IsSuccess)
        {
            return TypedResults.Ok(WorkItemEndpoints.ToResponse(engine.Project(result.WorkItem!)));
        }

        if (result.FailureCode == WorkItemActionFailureCode.WorkItemNotFound)
        {
            return TypedResults.NotFound();
        }

        var continueStatus = result.FailureCode switch
        {
            WorkItemActionFailureCode.MissingActorIdentity => StatusCodes.Status401Unauthorized,
            WorkItemActionFailureCode.InvalidTransition
            or WorkItemActionFailureCode.ConcurrencyConflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };

        return TypedResults.Problem(
            title: "Could not continue re-accreditation review",
            detail: result.Message,
            statusCode: continueStatus
        );
    }

    /// <summary>
    /// RA-294/RA-297: record that the operator backend added a new Overseas
    /// Reprocessing Site (ORS) or interim site (a waste staging point linked
    /// 1:1 to an ORS) to a re-accreditation application. This repo never
    /// models ORS/interim-site detail itself (<see cref="WorkItem.Payload"/>
    /// stays schemaless BSON) — the only side effect is a <c>site-added</c>
    /// audit-log entry so the event is visible on the work item's detail/
    /// audit-log page.
    ///
    /// There is no state transition, so unlike <see cref="QueryApplication"/>
    /// and <see cref="ResumeFromQuery"/> there is no state-derived 409 — the
    /// only failure paths are a malformed body (400), an unknown work item
    /// (404), and a concurrency-exhausted audit append (409).
    /// </summary>
    public static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> SiteAdded(
        [FromRoute] Guid id,
        SiteAddedRequest request,
        HttpContext httpContext,
        [FromServices] IReAccreditationSiteAddedService siteAddedService,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    )
    {
        if (ReAccreditationSiteAddedValidator.Validate(request) is { } validationError)
        {
            return TypedResults.Problem(
                title: "Invalid site-added notification",
                detail: validationError,
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var result = await siteAddedService.RecordSiteAddedAsync(
            id,
            request,
            httpContext.User,
            cancellationToken
        );

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
            WorkItemActionFailureCode.ConcurrencyConflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };

        return TypedResults.Problem(
            title: "Could not record site added",
            detail: result.Message,
            statusCode: status
        );
    }
}

internal sealed record ReAccreditationRecommendationResponse(
    string Recommendation,
    string Rationale
);

/// <summary>Request body for <see cref="ReAccreditationEndpoints.RecordDecisionRationale"/>.</summary>
internal sealed record DecisionRationaleRequest(string Rationale);

/// <summary>
/// RA-410 request body for <see cref="ReAccreditationEndpoints.LogDecision"/>.
/// Nullable so a missing or malformed body reaches the endpoint's own
/// validation (and its machine-readable <c>errorCode</c>) rather than being
/// rejected upstream as an unbindable model.
/// </summary>
internal sealed record LogDecisionRequest(string? Outcome);

internal static partial class ReAccreditationEndpointsRationale
{
    /// <summary>
    /// Minimum rationale length. Picked to force assessors to write a real
    /// sentence rather than a one-character placeholder, while still
    /// permitting short "approved — meets all criteria" decisions.
    /// </summary>
    public const int MinRationaleLength = 10;
}
