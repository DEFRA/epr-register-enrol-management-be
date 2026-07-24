using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-311/MBE-1 module-scoped service that resumes a queried re-accreditation
/// application once the operator backend confirms a resubmission. Module DI
/// uses module-scoped interfaces so the re-accreditation folder stays
/// self-contained (mirrors <see cref="IReAccreditationQueryService"/>).
/// </summary>
internal interface IReAccreditationResumeService
{
    /// <summary>
    /// Record the operator's current section values and file references,
    /// then move the work item out of <c>queried</c> back into whichever
    /// state it was queried from. The caller never supplies an action id:
    /// the correct <c>resume-during-*</c> transition is resolved from the
    /// work item's own <c>application-queried</c> audit history.
    ///
    /// Idempotent: a work item that has already left <c>queried</c> (a
    /// duplicate/retried call) succeeds as an
    /// <see cref="WorkItemActionResult.IsIdempotentReplay">idempotent
    /// replay</see> rather than failing, so a genuinely concurrent duplicate
    /// resubmit cannot strand the caller's own retry logic.
    /// </summary>
    Task<WorkItemActionResult> ResumeFromQueryAsync(
        Guid workItemId,
        ResumeFromQueryRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);
}
