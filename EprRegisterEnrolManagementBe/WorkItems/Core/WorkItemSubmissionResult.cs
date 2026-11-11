namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-739: one work item created from a submission, with the
/// <c>siteId</c> of the site it holds and its <c>applicationReference</c>.
/// </summary>
public sealed record SubmittedSiteWorkItem(
    string? SiteId,
    WorkItem WorkItem,
    string ApplicationReference
);

/// <summary>
/// RA-739: result of <see cref="IWorkItemService.SubmitSubmissionAsync"/>.
/// Either the shared <see cref="SubmissionReference"/> and the work items it
/// created, in site order, or the <see cref="WorkItemActionResult"/> failure
/// that stopped it.
/// </summary>
public sealed record WorkItemSubmissionResult
{
    private WorkItemSubmissionResult(
        string? submissionReference,
        IReadOnlyList<SubmittedSiteWorkItem> workItems,
        WorkItemActionResult? failure
    )
    {
        SubmissionReference = submissionReference;
        WorkItems = workItems;
        FailureResult = failure;
    }

    public string? SubmissionReference { get; }
    public IReadOnlyList<SubmittedSiteWorkItem> WorkItems { get; }

    /// <summary>The failed <see cref="WorkItemActionResult"/>, or <c>null</c> on success.</summary>
    public WorkItemActionResult? FailureResult { get; }

    public bool IsSuccess => FailureResult is null;

    public static WorkItemSubmissionResult Success(
        string submissionReference,
        IReadOnlyList<SubmittedSiteWorkItem> workItems
    ) => new(submissionReference, workItems, failure: null);

    public static WorkItemSubmissionResult Failure(WorkItemActionResult failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.IsSuccess)
        {
            throw new ArgumentException("A failure result is required.", nameof(failure));
        }
        return new(submissionReference: null, [], failure);
    }
}
