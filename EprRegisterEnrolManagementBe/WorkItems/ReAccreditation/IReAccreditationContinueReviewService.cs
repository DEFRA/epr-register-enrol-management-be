using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-337 module-scoped service that moves a re-accreditation work item on
/// from the non-terminal <c>updated</c> state once a caseworker has reviewed
/// a query resubmission. Module DI uses module-scoped interfaces so the
/// re-accreditation folder stays self-contained (mirrors
/// <see cref="IReAccreditationResumeService"/>).
/// </summary>
internal interface IReAccreditationContinueReviewService
{
    /// <summary>
    /// Move the work item out of <c>updated</c> back into whichever state it
    /// was originally queried from. The caller never supplies an action id:
    /// the correct <c>continue-review-during-*</c> transition is resolved
    /// from the work item's own <c>resume-during-*</c> audit history.
    ///
    /// Idempotent: a work item that has already left <c>updated</c> (a
    /// duplicate/retried call) succeeds as an
    /// <see cref="WorkItemActionResult.IsIdempotentReplay">idempotent
    /// replay</see> rather than failing.
    /// </summary>
    Task<WorkItemActionResult> ContinueReviewAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);
}
