namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-739: result of <see cref="IWorkItemService.SubmitPerSiteAsync"/>.
/// Either the stored <see cref="Submission"/> (newly created, or an earlier
/// one returned as an idempotent replay), or the
/// <see cref="WorkItemActionFailureCode"/> and message that stopped it.
/// Shaped like <see cref="WorkItemActionResult"/>, so callers check failure
/// the same way for both.
/// </summary>
public sealed record WorkItemSubmissionResult
{
    private WorkItemSubmissionResult(
        SubmissionRecord? submission,
        WorkItemActionFailureCode? failureCode,
        string? message,
        bool isIdempotentReplay
    )
    {
        Submission = submission;
        FailureCode = failureCode;
        Message = message;
        IsIdempotentReplay = isIdempotentReplay;
    }

    /// <summary>The submission and its work items, in site order, or <c>null</c> on failure.</summary>
    public SubmissionRecord? Submission { get; }

    /// <summary>Why the submission failed, or <c>null</c> on success.</summary>
    public WorkItemActionFailureCode? FailureCode { get; }

    /// <summary>A human-readable description of the failure, or <c>null</c> on success.</summary>
    public string? Message { get; }

    /// <summary>
    /// True when the <c>operatorRequestId</c> was already on file and
    /// <see cref="Submission"/> is the earlier submission, so nothing was
    /// created.
    /// </summary>
    public bool IsIdempotentReplay { get; }

    public bool IsSuccess => FailureCode is null;

    public static WorkItemSubmissionResult Success(SubmissionRecord submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        return new(submission, failureCode: null, message: null, isIdempotentReplay: false);
    }

    public static WorkItemSubmissionResult IdempotentReplay(SubmissionRecord submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        return new(submission, failureCode: null, message: null, isIdempotentReplay: true);
    }

    public static WorkItemSubmissionResult Failure(WorkItemActionFailureCode code, string message) =>
        new(submission: null, code, message, isIdempotentReplay: false);

    /// <summary>Carry a failed <see cref="WorkItemActionResult"/>'s code and message over.</summary>
    public static WorkItemSubmissionResult Failure(WorkItemActionResult failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.FailureCode is not { } code)
        {
            throw new ArgumentException("A failure result is required.", nameof(failure));
        }
        return Failure(code, failure.Message ?? string.Empty);
    }
}
