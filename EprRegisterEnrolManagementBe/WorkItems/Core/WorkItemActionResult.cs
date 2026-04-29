namespace EprRegisterEnrolManagementBe.WorkItems.Core;

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
    InvalidAssignment,
    /// <summary>
    /// A request to add a note was structurally invalid (e.g. blank text or
    /// over the size limit).
    /// </summary>
    InvalidNote,
    /// <summary>
    /// The work item was modified by another caller between load and save
    /// (optimistic concurrency conflict). Retry the request after re-reading
    /// the latest state.
    /// </summary>
    ConcurrencyConflict,
    /// <summary>
    /// The caller did not present an end-user identity (the BFF must
    /// forward a <c>user:id</c> claim). Mutating operations refuse to write
    /// audit entries that cannot be tied back to a real human, so without
    /// this claim we 401 the request rather than persist a placeholder.
    /// </summary>
    MissingActorIdentity
}

/// <summary>
/// Result of a state- or task-changing operation. Either succeeds with the
/// updated <see cref="WorkItem"/>, or fails with a <see cref="WorkItemActionFailureCode"/>
/// and human-readable message.
/// </summary>
public sealed record WorkItemActionResult
{
    private WorkItemActionResult(
        WorkItem? workItem,
        WorkItemActionFailureCode? failureCode,
        string? message,
        bool isIdempotentReplay)
    {
        WorkItem = workItem;
        FailureCode = failureCode;
        Message = message;
        IsIdempotentReplay = isIdempotentReplay;
    }

    public WorkItem? WorkItem { get; }
    public WorkItemActionFailureCode? FailureCode { get; }
    public string? Message { get; }

    /// <summary>
    /// True when this success is the second-or-later call that performed
    /// the same action — no state changed and no audit entry was written
    /// because the operation had already been applied. Endpoints surface
    /// this via the <c>X-Idempotent-Replay: true</c> response header so
    /// clients can distinguish "first hit" from "replay".
    /// </summary>
    public bool IsIdempotentReplay { get; }

    public bool IsSuccess => FailureCode is null;

    public static WorkItemActionResult Success(WorkItem workItem) =>
        new(workItem, failureCode: null, message: null, isIdempotentReplay: false);

    /// <summary>
    /// Same as <see cref="Success"/> but flags the result as a no-op replay
    /// of an already-applied action.
    /// </summary>
    public static WorkItemActionResult IdempotentReplay(WorkItem workItem) =>
        new(workItem, failureCode: null, message: null, isIdempotentReplay: true);

    public static WorkItemActionResult Failure(WorkItemActionFailureCode code, string message) =>
        new(workItem: null, code, message, isIdempotentReplay: false);
}
