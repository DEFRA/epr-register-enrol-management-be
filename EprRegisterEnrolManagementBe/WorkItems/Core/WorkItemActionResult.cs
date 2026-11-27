namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Failure reasons returned by the work item engine. Endpoints translate
/// these into HTTP problem responses; module service objects can branch on
/// them to decide what to show the user.
/// </summary>
public enum WorkItemActionFailureCode
{
    WorkItemNotFound,
    UnknownAction,
    InvalidTransition,
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
    MissingActorIdentity,

    /// <summary>
    /// The engine could not allocate a unique <c>applicationReference</c>
    /// within its bounded retry budget (RA-219). This is a transient
    /// server-side condition rather than a client error, so the endpoint maps
    /// it to a 503 and the caller can safely retry the submission.
    /// </summary>
    ApplicationReferenceExhausted,

    /// <summary>
    /// RA-492: the submitted payload lacks a field the type's
    /// <c>applicationReference</c> format needs (an in-year change type's
    /// 4-digit <c>accreditationYear</c>). Nothing was allocated or persisted.
    /// A client error, so the submit endpoint maps it to a 400.
    /// </summary>
    InvalidSubmissionPayload,

    /// <summary>
    /// A mandatory upstream notification a mutation is gated on could not be
    /// delivered within its retry budget, so the mutation was abandoned before
    /// anything was persisted (epr-p86e / RA-410: the operator-journey status
    /// push that the re-accreditation decision is gated on). No state changed —
    /// the caller may retry. Distinct from a client error: the endpoint maps it
    /// to a generic 500 rather than a 4xx, because the request itself was
    /// well-formed and the failure is a server-side dependency being
    /// unreachable.
    /// </summary>
    UpstreamNotificationFailed,

    /// <summary>
    /// RA-448 phase 2: the backend call to resolve a real accreditation
    /// number failed or timed out, so re-accreditation approval was
    /// abandoned before anything was persisted. Kept distinct from
    /// <see cref="UpstreamNotificationFailed"/> (a different dependency, the
    /// operator-journey status push on /decision) so logs/alerts/error
    /// codes keyed on this enum can tell the two outages apart. No state
    /// changed — the caller may retry. Maps to a generic 500, not a 4xx, for
    /// the same reason as <see cref="UpstreamNotificationFailed"/>.
    /// </summary>
    AccreditationNumberUnavailable,

    /// <summary>
    /// RA-739: the <c>operatorRequestId</c> on a submission is already on file
    /// for a different work item type. It is the caller's idempotency key, and
    /// the replay lookup matches on it alone, so replaying the earlier
    /// submission would answer a different request with someone else's
    /// references and the caller could not tell its own was ignored. Nothing
    /// was created; the caller must submit with its own key. Maps to a 409,
    /// since retrying the same key cannot succeed.
    /// </summary>
    SubmissionRequestIdReused,

    /// <summary>
    /// RA-495 review: the work item's rules cannot be resolved, because its
    /// type is not registered and it has no stored template snapshot
    /// (<see cref="WorkItemEngineRules.ResolveTemplate"/> returned
    /// <c>null</c>). Raised by the shared Standard-lifecycle services for a
    /// type that has opted in. A server-side configuration problem, not a
    /// wrong type or a malformed request, so it is kept distinct from
    /// <see cref="UnknownAction"/> (which those services' endpoints label
    /// <c>wrong-work-item-type</c>) and maps to a generic 500 with no
    /// <c>errorCode</c>. Nothing was changed.
    /// </summary>
    TemplateUnavailable,

    /// <summary>
    /// RA-495 AC6 / RA-795: the effective date sent with an add-ORS approval is
    /// missing, or outside 1 January to 31 December of the work item's
    /// accreditation year.
    /// A client error (400) whose <see cref="WorkItemActionResult.ErrorCode"/>
    /// names the rule, so the frontend binds it to the date field without
    /// parsing prose. Nothing was changed.
    /// </summary>
    InvalidEffectiveDate,

    /// <summary>
    /// A duly make whose payment date does not suit the type: missing for a
    /// type that takes one, or sent for a type that takes none
    /// (<c>IStandardDulyMakingRules.TakesPaymentDate</c>). A client error (400)
    /// whose <see cref="WorkItemActionResult.ErrorCode"/> the frontend binds to
    /// the payment date field. Nothing was changed.
    /// </summary>
    InvalidPaymentDate,

    /// <summary>
    /// RA-795: a valid-from date could not be checked against the accreditation
    /// year because the work item's <c>payload.accreditationYear</c> is missing
    /// or unreadable. Mandatory at submission, so a server-side data fault
    /// (a generic 500 with no <c>errorCode</c>), not a client error. Nothing
    /// was changed.
    /// </summary>
    AccreditationYearUnavailable,
}

/// <summary>
/// Result of a state-changing operation. Either succeeds with the
/// updated <see cref="WorkItem"/>, or fails with a <see cref="WorkItemActionFailureCode"/>
/// and human-readable message.
/// </summary>
public sealed record WorkItemActionResult
{
    private WorkItemActionResult(
        WorkItem? workItem,
        WorkItemActionFailureCode? failureCode,
        string? message,
        bool isIdempotentReplay,
        string? errorCode = null
    )
    {
        WorkItem = workItem;
        FailureCode = failureCode;
        Message = message;
        IsIdempotentReplay = isIdempotentReplay;
        ErrorCode = errorCode;
    }

    public WorkItem? WorkItem { get; }
    public WorkItemActionFailureCode? FailureCode { get; }
    public string? Message { get; }

    /// <summary>
    /// The wire <c>errorCode</c> for a failure the frontend binds to a form
    /// field (RA-495 AC6: the effective date). Null for every other failure.
    /// </summary>
    public string? ErrorCode { get; }

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

    /// <summary>A failure carrying a wire <see cref="ErrorCode"/> the frontend binds to a field.</summary>
    public static WorkItemActionResult Failure(WorkItemActionFailureCode code, string message, string errorCode) =>
        new(workItem: null, code, message, isIdempotentReplay: false, errorCode);
}
