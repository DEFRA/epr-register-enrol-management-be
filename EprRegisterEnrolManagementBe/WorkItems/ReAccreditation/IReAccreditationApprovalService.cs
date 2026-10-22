using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-132: module-scoped service object that owns approval of a
/// re-accreditation work item. Approval is more than a generic state
/// transition — it issues an accreditation id, stamps the SLA-clock-stopped
/// timestamp, writes three audit entries atomically, and fans out
/// post-action hooks plus a queued publishing job. RA-527: approval is now
/// two steps. <see cref="RecordApprovedDeterminationAsync"/> is step 1, called
/// by <see cref="IReAccreditationLogDecisionService"/> (the /decision
/// endpoint); <see cref="ApproveAsync"/> applies the outcome and is step 2
/// (confirm applicant contact, wired in task 004). Wrapped behind an
/// interface so it can be substituted in tests.
/// </summary>
internal interface IReAccreditationApprovalService
{
    /// <summary>
    /// Approve the re-accreditation work item identified by
    /// <paramref name="workItemId"/>. Returns a generic
    /// <see cref="WorkItemActionResult"/> so the caller can
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
    /// RA-527 step 1 of an approval: stamp <paramref name="accreditationNumber"/>
    /// (already minted by the caller via <see cref="ResolveAccreditationNumberAsync"/>),
    /// its start date and year on the payload, and move the work item from
    /// <c>awaiting-decision</c> to the non-terminal <c>approved-pending-contact</c>
    /// state in one write, with a "Determination made: Approved" audit entry.
    ///
    /// The outcome is not applied: the SLA clock is not stopped, no
    /// accreditation-issued entry is written and no publishing job is queued.
    /// Post-action hooks run with action id <c>make-determination-approved</c>;
    /// none of them pushes or emails for it. An item already in
    /// <c>approved-pending-contact</c> is an idempotent replay.
    /// </summary>
    Task<WorkItemActionResult> RecordApprovedDeterminationAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        AccreditationNumberResult accreditationNumber,
        CancellationToken cancellationToken = default
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
