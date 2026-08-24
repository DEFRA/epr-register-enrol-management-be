using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
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
    ///
    /// epr-r9oy: <paramref name="preResolvedAccreditationNumber"/> lets a
    /// caller (currently only <see cref="IReAccreditationLogDecisionService"/>)
    /// supply a number already resolved via <see cref="ResolveAccreditationNumberAsync"/>
    /// — required when the caller must mint the number BEFORE this application
    /// is marked terminal elsewhere (the backend's accreditation-number endpoint
    /// refuses once it is). Omitted, this method resolves its own number exactly
    /// as before.
    /// </summary>
    Task<WorkItemActionResult> ApproveAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default,
        AccreditationNumberResult? preResolvedAccreditationNumber = null
    );

    /// <summary>
    /// epr-r9oy: resolves (mints, or returns the already-issued) accreditation
    /// number for <paramref name="workItemId"/> WITHOUT transitioning the work
    /// item or writing anything — the same backend call <see cref="ApproveAsync"/>
    /// makes internally, exposed standalone so a caller can mint it while the
    /// application is still open, ahead of an operator-journey push that would
    /// otherwise mark it terminal first and make the backend refuse the mint.
    /// </summary>
    Task<AccreditationNumberResult> ResolveAccreditationNumberAsync(
        Guid workItemId,
        Guid correlationId,
        CancellationToken cancellationToken = default
    );
}
