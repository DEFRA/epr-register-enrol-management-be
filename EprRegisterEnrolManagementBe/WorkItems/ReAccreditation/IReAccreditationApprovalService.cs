using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-132: module-scoped service object that owns approval of a
/// re-accreditation work item. Approval is more than a generic state
/// transition — it issues an accreditation id, stops the SLA clock, writes
/// its audit entries atomically, and fans out post-action hooks plus a queued
/// publishing job. RA-527: approval is two steps.
/// <see cref="RecordApprovedDeterminationAsync"/> is step 1, called by
/// <see cref="IReAccreditationLogDecisionService"/> (the /decision endpoint);
/// <see cref="ApproveAsync"/> applies the outcome and is step 2, called by
/// <see cref="IReAccreditationConfirmContactService"/> (the /confirm-contact
/// endpoint). Wrapped behind an interface so it can be substituted in tests.
/// </summary>
internal interface IReAccreditationApprovalService
{
    /// <summary>
    /// RA-527 step 2 of an approval ("confirm applicant contact"): move the
    /// work item from <c>approved-pending-contact</c> to <c>approved</c>
    /// ("Granted") in one write — the SLA clock stopped at
    /// <paramref name="appliedAt"/>, an "Applicant contacted" audit entry for
    /// <paramref name="user"/> and the action-applied, sla-clock-stopped and
    /// accreditation-issued entries — then queue publishing and run the
    /// post-action hooks (the Decision email).
    ///
    /// Reuses the accreditation number step 1 stamped; never mints one.
    /// <paramref name="appliedAt"/> is the caller's single timestamp for the
    /// whole step, also sent to the operator journey as the push's
    /// <c>OccurredAt</c>. An item already <c>approved</c> is an idempotent
    /// replay with no side effects. Returns a generic
    /// <see cref="WorkItemActionResult"/> so the caller can reuse the same
    /// problem-mapping switch the framework uses.
    /// </summary>
    Task<WorkItemActionResult> ApproveAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        DateTimeOffset appliedAt,
        CancellationToken cancellationToken = default
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
    /// item or writing anything. RA-527: step 1 of an approval mints it here,
    /// while the application is still open — the backend's number endpoint
    /// refuses once the operator journey has marked it terminal — and
    /// <see cref="RecordApprovedDeterminationAsync"/> stamps it.
    /// </summary>
    Task<AccreditationNumberResult> ResolveAccreditationNumberAsync(
        Guid workItemId,
        Guid correlationId,
        CancellationToken cancellationToken = default
    );
}
