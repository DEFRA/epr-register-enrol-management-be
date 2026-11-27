using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;

/// <summary>
/// RA-698: the add-ORS query and response endpoints.
/// <list type="bullet">
///   <item><c>POST /work-items/{id}/query</c>: the regulator raises a
///   reason-only query (type-less path, like the Standard lifecycle routes; a
///   work item of another type is refused).</item>
///   <item><c>POST /work-items/add-overseas-site/{id}/resume-from-query</c>:
///   called by the operator backend once the operator has answered.</item>
///   <item><c>POST /work-items/{id}/continue-review</c>: the caseworker moves
///   an updated item on once the response has been reviewed.</item>
/// </list>
/// The caller never names an action: each resolves its transition from the
/// item's own state or audit history.
/// </summary>
internal static class AddOverseasSiteEndpoints
{
    /// <summary>
    /// A legitimate query body is a reason of at most 200 words; the endpoint
    /// calls <c>.DisableValidation()</c> so it carries its own size guard.
    /// </summary>
    public const long MaxQueryBodyBytes = 16 * 1024;

    /// <summary>
    /// A resume body carries changed field values and BES file references, not
    /// whole sections; 256 KiB is generous. It calls <c>.DisableValidation()</c>
    /// so it carries its own size guard.
    /// </summary>
    public const long MaxResumeBodyBytes = 256 * 1024;

    public const string QueryProblemTitle = "Could not query overseas reprocessing site";
    public const string ResumeProblemTitle = "Could not resume overseas reprocessing site from query";
    public const string ContinueReviewProblemTitle = "Could not continue review";

    public static IEndpointRouteBuilder MapAddOverseasSiteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/work-items/{id:guid}/query", Query)
            .WithTags("WorkItems")
            .WithName("QueryAddOverseasSite")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxQueryBodyBytes))
            .RequireAuthorization();

        app.MapPost("/work-items/{id:guid}/continue-review", ContinueReview)
            .WithTags("WorkItems")
            .WithName("ContinueAddOverseasSiteReview")
            .RequireAuthorization();

        app.MapPost("/work-items/add-overseas-site/{id:guid}/resume-from-query", ResumeFromQuery)
            .WithTags("AddOverseasSite")
            .WithName("ResumeAddOverseasSiteFromQuery")
            .DisableValidation()
            .WithMetadata(new RequestSizeLimitAttribute(MaxResumeBodyBytes))
            .RequireAuthorization();

        return app;
    }

    public static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> Query(
        [FromRoute] Guid id,
        AddOverseasSiteQueryRequest request,
        HttpContext httpContext,
        [FromServices] IAddOverseasSiteQueryService queryService,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    )
    {
        if (AddOverseasSiteQueryValidator.Validate(request) is { } validationError)
        {
            return TypedResults.Problem(
                title: "Invalid query",
                detail: validationError,
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var result = await queryService.QueryAsync(id, request.Reason!.Trim(), httpContext.User, cancellationToken);
        return ToResult(result, engine, QueryProblemTitle);
    }

    public static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> ResumeFromQuery(
        [FromRoute] Guid id,
        AddOverseasSiteResumeRequest request,
        HttpContext httpContext,
        [FromServices] IAddOverseasSiteResumeService resumeService,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    )
    {
        if (AddOverseasSiteResumeValidator.Validate(request) is { } validationError)
        {
            return TypedResults.Problem(
                title: "Invalid resume-from-query request",
                detail: validationError,
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var result = await resumeService.ResumeFromQueryAsync(id, request, httpContext.User, cancellationToken);
        return ToResult(result, engine, ResumeProblemTitle);
    }

    public static async Task<Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult>> ContinueReview(
        [FromRoute] Guid id,
        HttpContext httpContext,
        [FromServices] IAddOverseasSiteContinueReviewService continueReviewService,
        [FromServices] IWorkItemService engine,
        CancellationToken cancellationToken
    )
    {
        var result = await continueReviewService.ContinueReviewAsync(id, httpContext.User, cancellationToken);
        if (result.IsIdempotentReplay)
        {
            httpContext.Response.Headers[WorkItemEndpoints.IdempotentReplayHeader] = "true";
        }

        return ToResult(result, engine, ContinueReviewProblemTitle);
    }

    // A wrong state, or a racing writer, is a conflict with the resource, not
    // a malformed request; a work item of the wrong type is a 400.
    private static Results<Ok<WorkItemResponse>, NotFound, ProblemHttpResult> ToResult(
        WorkItemActionResult result,
        IWorkItemService engine,
        string problemTitle
    )
    {
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

        return TypedResults.Problem(title: problemTitle, detail: result.Message, statusCode: status);
    }
}
