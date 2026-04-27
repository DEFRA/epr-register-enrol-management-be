namespace Backend.Api.WorkItems.Core;

/// <summary>
/// Failure reasons returned by the work item engine. Endpoints translate
/// these into HTTP problem responses; module service objects can branch on
/// them to decide what to show the user.
/// </summary>
public enum WorkItemActionFailureCode
{
    WorkItemNotFound,
    TaskNotApplicable,
    UnknownAction,
    InvalidTransition,
    IncompleteTasks,
    TerminalState,
    /// <summary>
    /// The caller is not allowed to perform this assignment (e.g. a standard
    /// user trying to assign someone else, or to take an item that is already
    /// assigned to a different user).
    /// </summary>
    NotAuthorized,
    /// <summary>
    /// The assign request was structurally invalid (e.g. blank assignee id).
    /// </summary>
    InvalidAssignment
}

/// <summary>
/// Result of a state- or task-changing operation. Either succeeds with the
/// updated <see cref="WorkItem"/>, or fails with a <see cref="WorkItemActionFailureCode"/>
/// and human-readable message.
/// </summary>
public sealed record WorkItemActionResult
{
    private WorkItemActionResult(WorkItem? workItem, WorkItemActionFailureCode? failureCode, string? message)
    {
        WorkItem = workItem;
        FailureCode = failureCode;
        Message = message;
    }

    public WorkItem? WorkItem { get; }
    public WorkItemActionFailureCode? FailureCode { get; }
    public string? Message { get; }

    public bool IsSuccess => FailureCode is null;

    public static WorkItemActionResult Success(WorkItem workItem) =>
        new(workItem, failureCode: null, message: null);

    public static WorkItemActionResult Failure(WorkItemActionFailureCode code, string message) =>
        new(workItem: null, code, message);
}
