using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-132: module-scoped service object that owns approval of a
/// re-accreditation work item. Approval is more than a generic state
/// transition — it issues an accreditation id, stamps the SLA-clock-stopped
/// timestamp, writes three audit entries atomically, and fans out
/// post-action hooks plus a queued publishing job. Wrapped behind an
/// interface so endpoints can be tested with a substitute.
/// </summary>
internal interface IReAccreditationApprovalService
{
    /// <summary>
    /// Approve the re-accreditation work item identified by
    /// <paramref name="workItemId"/>. Returns a generic
    /// <see cref="WorkItemActionResult"/> so the calling endpoint can
    /// reuse the same problem-mapping switch the framework uses.
    /// </summary>
    Task<WorkItemActionResult> ApproveAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);
}
