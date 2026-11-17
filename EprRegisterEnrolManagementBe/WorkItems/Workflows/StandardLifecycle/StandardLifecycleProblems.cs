using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-495: the ProblemDetails a failed lifecycle step maps to, shared by the
/// Standard-workflow endpoints and re-accreditation's own routes so both speak
/// the same failure vocabulary to management-fe.
/// </summary>
internal static class StandardLifecycleProblems
{
    public const string ErrorCodeKey = "errorCode";
    public const string FieldKey = "field";
    public const string WrongWorkItemTypeErrorCode = "wrong-work-item-type";

    /// <summary>
    /// A failed duly-make (or change-payment-date) service result. 401 for a
    /// missing actor; 409 for the conflicts with the resource's current state
    /// (the frontend's answer is "this application has changed, reload it",
    /// never a field error); 400 otherwise. The only 400 a service itself
    /// produces is a type mismatch, which gets an <c>errorCode</c> so the
    /// frontend never parses prose.
    /// </summary>
    public static ProblemHttpResult ServiceFailure(WorkItemActionResult result, string title)
    {
        var status = result.FailureCode switch
        {
            WorkItemActionFailureCode.MissingActorIdentity => StatusCodes.Status401Unauthorized,
            WorkItemActionFailureCode.InvalidTransition
            or WorkItemActionFailureCode.TerminalState
            or WorkItemActionFailureCode.ConcurrencyConflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };

        return TypedResults.Problem(
            title: title,
            detail: result.Message,
            statusCode: status,
            extensions: WrongTypeExtensions(result)
        );
    }

    /// <summary>
    /// A failed determination step (make determination, confirm contact). As
    /// <see cref="ServiceFailure"/>, plus 403 for not authorised and a generic
    /// 500 for an upstream dependency (the operator journey push, the
    /// accreditation-number call) that failed before anything was persisted.
    /// </summary>
    public static ProblemHttpResult DecisionFailure(WorkItemActionResult result, string title)
    {
        var status = result.FailureCode switch
        {
            WorkItemActionFailureCode.MissingActorIdentity => StatusCodes.Status401Unauthorized,
            WorkItemActionFailureCode.NotAuthorized => StatusCodes.Status403Forbidden,
            WorkItemActionFailureCode.InvalidTransition
            or WorkItemActionFailureCode.TerminalState
            or WorkItemActionFailureCode.ConcurrencyConflict => StatusCodes.Status409Conflict,
            // epr-p86e / RA-410: the operator journey could not be notified,
            // so the step was abandoned before anything was persisted. RA-475:
            // likewise the accreditation-number call. A server-side dependency
            // problem, not a malformed request, so a generic 500 with no
            // errorCode: the frontend shows a generic try-again.
            WorkItemActionFailureCode.UpstreamNotificationFailed
            or WorkItemActionFailureCode.AccreditationNumberUnavailable =>
                StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status400BadRequest,
        };

        return TypedResults.Problem(
            title: title,
            detail: result.Message,
            statusCode: status,
            extensions: WrongTypeExtensions(result)
        );
    }

    private static Dictionary<string, object?>? WrongTypeExtensions(WorkItemActionResult result) =>
        result.FailureCode == WorkItemActionFailureCode.UnknownAction
            ? new Dictionary<string, object?> { [ErrorCodeKey] = WrongWorkItemTypeErrorCode }
            : null;
}
