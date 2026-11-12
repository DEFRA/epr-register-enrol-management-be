namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-739: result of <see cref="IWorkItemService.SubmitPerSiteAsync"/>.
/// Either the stored <see cref="Submission"/> (newly created, or an earlier
/// one returned as an idempotent replay), or the
/// <see cref="WorkItemActionResult"/> failure that stopped it.
/// </summary>
public sealed record WorkItemSubmissionResult
{
    private WorkItemSubmissionResult(
        SubmissionRecord? submission,
        bool isIdempotentReplay,
        WorkItemActionResult? failure
    )
    {
        Submission = submission;
        IsIdempotentReplay = isIdempotentReplay;
        FailureResult = failure;
    }

    /// <summary>The submission and its work items, in site order, or <c>null</c> on failure.</summary>
    public SubmissionRecord? Submission { get; }

    /// <summary>
    /// True when the <c>operatorRequestId</c> was already on file and
    /// <see cref="Submission"/> is the earlier submission, so nothing was
    /// created.
    /// </summary>
    public bool IsIdempotentReplay { get; }

    /// <summary>The failed <see cref="WorkItemActionResult"/>, or <c>null</c> on success.</summary>
    public WorkItemActionResult? FailureResult { get; }

    public bool IsSuccess => FailureResult is null;

    public static WorkItemSubmissionResult Success(SubmissionRecord submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        return new(submission, isIdempotentReplay: false, failure: null);
    }

    public static WorkItemSubmissionResult IdempotentReplay(SubmissionRecord submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        return new(submission, isIdempotentReplay: true, failure: null);
    }

    public static WorkItemSubmissionResult Failure(WorkItemActionResult failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.IsSuccess)
        {
            throw new ArgumentException("A failure result is required.", nameof(failure));
        }
        return new(submission: null, isIdempotentReplay: false, failure);
    }
}
