namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-739: the <c>POST /work-items</c> 201 body for a type that uses a
/// submission reference (add-ORS): the submission reference every work item
/// shares, and one entry per submitted site.
/// </summary>
public sealed record WorkItemSubmissionResponse(
    string SubmissionReference,
    IReadOnlyList<WorkItemSubmissionItemResponse> WorkItems
);

/// <summary>RA-739: the work item created for one submitted site.</summary>
public sealed record WorkItemSubmissionItemResponse(
    string? SiteId,
    Guid Id,
    string ApplicationReference
);
