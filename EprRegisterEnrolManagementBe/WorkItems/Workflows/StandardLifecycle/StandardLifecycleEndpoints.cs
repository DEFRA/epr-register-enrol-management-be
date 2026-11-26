using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-495: the Standard-workflow lifecycle endpoints, for any work item whose
/// type has opted in (<see cref="IStandardLifecycleStrategy"/>). The type is
/// read off the work item, so the routes carry none. A work item of a type
/// that has not opted in gets a 400 with <c>errorCode: wrong-work-item-type</c>,
/// as re-accreditation's own routes give for another type.
///
/// Re-accreditation keeps its own routes
/// (<c>/work-items/re-accreditation/{id}/duly-make|decision|confirm-contact</c>),
/// limited to re-accreditation: they hand its keyed services to the handlers
/// here (bd 3bb.42), so the request bodies, validation and failure vocabulary
/// are the same. Only the decision's problem title differs.
/// </summary>
internal static class StandardLifecycleEndpoints
{
    /// <summary>
    /// The duly-make and decision endpoints call <c>.DisableValidation()</c>
    /// (they own their validation so a 400 can carry an <c>errorCode</c> and
    /// <c>field</c>), so each carries its own size guard. A legitimate body is
    /// one short string.
    /// </summary>
    public const long MaxBodyBytes = 4 * 1024;

    public const string DulyMakeProblemTitle = "Could not complete duly making";
    public const string LogDecisionProblemTitle = "Could not record decision";
    public const string ConfirmContactProblemTitle = "Could not confirm applicant contact";

    public static IEndpointRouteBuilder MapStandardLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/work-items").WithTags("WorkItems");

        group
            .MapGet("/{id:guid}/duly-make", GetDulyMakeDetails)
            .WithName("GetWorkItemDulyMakeDetails")
            .RequireAuthorization();

        group
            .MapPost("/{id:guid}/duly-make", DulyMake)
            .WithName("DulyMakeWorkItem")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes))
            .RequireAuthorization();

        group
            .MapPost("/{id:guid}/decision", LogDecision)
            .WithName("LogWorkItemDecision")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes))
            .RequireAuthorization();

        group
            .MapPost("/{id:guid}/confirm-contact", ConfirmContact)
            .WithName("ConfirmWorkItemApplicantContact")
            .RequireAuthorization();

        return app;
    }

    /// <summary>
    /// What the caseworker needs on the Duly make page that the type supplies:
    /// the charge to match the payment against
    /// (<see cref="IStandardDulyMakingRules.ReadChargePence"/>).
    /// </summary>
    public static async Task<Results<Ok<DulyMakeDetailsResponse>, NotFound, ProblemHttpResult>> GetDulyMakeDetails(
        [FromRoute] Guid id,
        [FromServices] IWorkItemPersistence persistence,
        [FromServices] StandardLifecycleStrategies strategies,
        CancellationToken cancellationToken
    )
    {
        var workItem = await persistence.GetByIdAsync(id, cancellationToken);
        if (workItem is null)
        {
            return TypedResults.NotFound();
        }

        if (strategies.Find(workItem.TypeId) is not { } strategy)
        {
            return StandardLifecycleProblems.ServiceFailure(
                strategies.NotOptedIn(id, workItem.TypeId),
                DulyMakeProblemTitle
            );
        }

        var related = await RelatedWorkItemsAsync(workItem, persistence, strategies, cancellationToken);
        return TypedResults.Ok(new DulyMakeDetailsResponse(id, strategy.ReadChargePence(workItem), related));
    }

    /// <summary>
    /// RA-495: every work item created from the same submission as
    /// <paramref name="workItem"/>, itself included (one per site, matched on
    /// <c>payload.submissionReference</c>, in reference order), so the
    /// caseworker can see the whole payment (user decision 2026-11-18, as in
    /// Figma). Empty for a work item with no submission reference. Each
    /// item's charge comes from its own type's charge source.
    /// </summary>
    private static async Task<IReadOnlyList<RelatedWorkItemResponse>> RelatedWorkItemsAsync(
        WorkItem workItem,
        IWorkItemPersistence persistence,
        StandardLifecycleStrategies strategies,
        CancellationToken cancellationToken
    )
    {
        if (!SubmissionPayload.TryReadSubmissionReference(workItem.Payload, out var reference))
        {
            return [];
        }

        var siblings = await persistence.FindBySubmissionReferenceAsync(reference, cancellationToken);
        return siblings
            .Select(sibling => RelatedWorkItemResponse.From(sibling, strategies.Find(sibling.TypeId)))
            .ToList();
    }

    /// <summary>
    /// Duly make a work item with the payment date the caseworker entered.
    /// Payment-date validation happens here so a 400 can carry the stable
    /// <c>errorCode</c> and <c>field</c> management-fe binds to its error
    /// summary (<see cref="StandardDulyMakingValidator"/>).
    /// </summary>
    public static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> DulyMake(
        [FromRoute] Guid id,
        HttpContext httpContext,
        [FromBody] StandardDulyMakeRequest? request,
        [FromServices] IStandardDulyMakingService dulyMakingService,
        [FromServices] IWorkItemService engine,
        [FromServices] TimeProvider timeProvider,
        CancellationToken cancellationToken
    )
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var validation = StandardDulyMakingValidator.Validate(request?.PaymentDate, today);

        if (!validation.IsValid)
        {
            return TypedResults.Problem(
                title: DulyMakeProblemTitle,
                detail: validation.Detail,
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?>
                {
                    [StandardLifecycleProblems.ErrorCodeKey] = validation.ErrorCode,
                    [StandardLifecycleProblems.FieldKey] = StandardDulyMakingValidator.Field,
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

        return result.FailureCode == WorkItemActionFailureCode.WorkItemNotFound
            ? TypedResults.NotFound()
            : StandardLifecycleProblems.ServiceFailure(result, DulyMakeProblemTitle);
    }

    /// <summary>
    /// Record the determination (step 1 of two). The body names an outcome,
    /// <c>approved</c> or <c>rejected</c>, never a state or action. A repeat
    /// of the same outcome is an idempotent replay. RA-495 AC6: an approval of
    /// a type that takes one (add-ORS) also names its <c>effectiveDate</c>
    /// (<c>yyyy-MM-dd</c>).
    /// </summary>
    public static Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> LogDecision(
        [FromRoute] Guid id,
        [FromBody] StandardDecisionRequest? request,
        HttpContext httpContext,
        [FromServices] IStandardLogDecisionService logDecisionService,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    ) =>
        LogDecisionAsync(
            id,
            request,
            httpContext,
            logDecisionService,
            engine,
            LogDecisionProblemTitle,
            cancellationToken
        );

    /// <summary>
    /// The body of <see cref="LogDecision"/>, taking the problem title because
    /// re-accreditation's own route keeps its established one (bd 3bb.42).
    /// </summary>
    internal static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> LogDecisionAsync(
        Guid id,
        StandardDecisionRequest? request,
        HttpContext httpContext,
        IStandardLogDecisionService logDecisionService,
        IWorkItemService engine,
        string problemTitle,
        CancellationToken cancellationToken
    )
    {
        if (!TryParseOutcome(request?.Outcome, out var outcome))
        {
            return TypedResults.Problem(
                title: problemTitle,
                detail: "'outcome' is required and must be either 'approved' or 'rejected'.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?>
                {
                    [StandardLifecycleProblems.ErrorCodeKey] = "invalid-outcome",
                    [StandardLifecycleProblems.FieldKey] = "outcome",
                }
            );
        }

        if (!StandardEffectiveDate.TryParse(request?.EffectiveDate, out var effectiveDate))
        {
            return TypedResults.Problem(
                title: problemTitle,
                detail: $"'{StandardEffectiveDate.Field}' must be a real {StandardEffectiveDate.DateFormat} date.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?>
                {
                    [StandardLifecycleProblems.ErrorCodeKey] = StandardEffectiveDate.CodeInvalid,
                    [StandardLifecycleProblems.FieldKey] = StandardEffectiveDate.Field,
                }
            );
        }

        var result = await logDecisionService.LogDecisionAsync(
            id,
            outcome,
            effectiveDate,
            httpContext.User,
            cancellationToken
        );
        return ToDecisionResult(result, httpContext, engine, problemTitle);
    }

    /// <summary>
    /// Confirm the applicant has been contacted, which applies the
    /// determination (step 2). The operator journey is told first; if it cannot
    /// be, a generic 500 and nothing is applied.
    /// </summary>
    public static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> ConfirmContact(
        [FromRoute] Guid id,
        HttpContext httpContext,
        [FromServices] IStandardConfirmContactService confirmContactService,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    )
    {
        var result = await confirmContactService.ConfirmContactAsync(
            id,
            httpContext.User,
            cancellationToken
        );
        return ToDecisionResult(result, httpContext, engine, ConfirmContactProblemTitle);
    }

    private static Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult> ToDecisionResult(
        WorkItemActionResult result,
        HttpContext httpContext,
        IWorkItemService engine,
        string problemTitle
    )
    {
        if (result.IsIdempotentReplay)
        {
            httpContext.Response.Headers[WorkItemEndpoints.IdempotentReplayHeader] = "true";
        }

        if (result.IsSuccess)
        {
            return TypedResults.Ok(WorkItemEndpoints.ToResponse(engine.Project(result.WorkItem!)));
        }

        return result.FailureCode == WorkItemActionFailureCode.WorkItemNotFound
            ? TypedResults.NotFound()
            : StandardLifecycleProblems.DecisionFailure(result, problemTitle);
    }

    /// <summary>
    /// Binds the wire <c>outcome</c> string onto <see cref="StandardDecisionOutcome"/>.
    /// Case-insensitive,
    /// matching the JSON enum convention used elsewhere on the wire, but
    /// strictly two-valued: <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>
    /// alone would also accept "0"/"1" and any future member, which would let a
    /// caller reach an outcome the frontend never offers.
    /// </summary>
    private static bool TryParseOutcome(string? value, out StandardDecisionOutcome outcome)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "approved":
                outcome = StandardDecisionOutcome.Approved;
                return true;
            case "rejected":
                outcome = StandardDecisionOutcome.Rejected;
                return true;
            default:
                outcome = default;
                return false;
        }
    }
}

/// <summary>RA-495: <c>POST /work-items/{id}/duly-make</c> body.</summary>
internal sealed record StandardDulyMakeRequest(string? PaymentDate);

/// <summary>
/// RA-495: <c>POST /work-items/{id}/decision</c> body. <c>EffectiveDate</c>
/// (AC6, <c>yyyy-MM-dd</c>) is for an approval of a type that takes one (add-ORS).
/// </summary>
internal sealed record StandardDecisionRequest(string? Outcome, string? EffectiveDate = null);

/// <summary>
/// RA-495: <c>GET /work-items/{id}/duly-make</c>: the charge, in pence, the
/// caseworker matches the payment against (null when the work item carries
/// none), and every work item from the same submission, itself included.
/// </summary>
internal sealed record DulyMakeDetailsResponse(
    Guid WorkItemId,
    int? ChargePence,
    IReadOnlyList<RelatedWorkItemResponse> RelatedWorkItems
);
