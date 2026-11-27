using Microsoft.AspNetCore.Http.HttpResults;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-752: the ProblemDetails for a work item id that matches no work item,
/// shared by the framework's and the modules' read routes. The
/// <c>errorCode</c> lets a caller (the Admin UI) tell "no such work item" from
/// any other 404, such as a route or proxy miss, without parsing prose.
/// </summary>
internal static class WorkItemProblems
{
    public const string ErrorCodeKey = "errorCode";
    public const string WorkItemNotFoundErrorCode = "work-item-not-found";

    public static ProblemHttpResult WorkItemNotFound(Guid id) =>
        TypedResults.Problem(
            title: "Work item not found",
            detail: $"There is no work item with id {id}.",
            statusCode: StatusCodes.Status404NotFound,
            extensions: new Dictionary<string, object?>
            {
                [ErrorCodeKey] = WorkItemNotFoundErrorCode,
            }
        );
}
