using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-527: module-scoped service object that owns both steps of a refusal,
/// the counterpart of <see cref="IReAccreditationApprovalService"/>. Each step
/// is one write with its own audit entries, which the generic engine cannot
/// make. <see cref="RecordRefusedDeterminationAsync"/> is step 1, called by
/// <see cref="IReAccreditationLogDecisionService"/> (the /decision endpoint);
/// <see cref="RefuseAsync"/> applies the outcome and is step 2, called by
/// <see cref="IReAccreditationConfirmContactService"/> (the /confirm-contact
/// endpoint). Wrapped behind an interface so it can be substituted in tests.
/// </summary>
internal interface IReAccreditationRefusalService
{
    /// <summary>
    /// RA-527 step 1 of a refusal: move the work item from
    /// <c>awaiting-decision</c> to the non-terminal
    /// <c>rejected-pending-contact</c> ("Refused") state in one write, with
    /// a "Determination made: Refused" audit entry.
    ///
    /// The outcome is not applied: no number is minted, the SLA clock is not
    /// stopped and nothing is pushed. Post-action hooks run with action id
    /// <c>make-determination-rejected</c>; none of them pushes or emails for
    /// it. An item already in <c>rejected-pending-contact</c> is an
    /// idempotent replay.
    /// </summary>
    Task<WorkItemActionResult> RecordRefusedDeterminationAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// RA-527 step 2 of a refusal ("confirm applicant contact"): move the work
    /// item from <c>rejected-pending-contact</c> to <c>rejected</c>
    /// ("Refused") in one write — the SLA clock stopped at
    /// <paramref name="appliedAt"/>, an "Applicant contacted" audit entry for
    /// <paramref name="user"/> and the action-applied and sla-clock-stopped
    /// entries — then run the post-action hooks. No email is sent (RA-211)
    /// and nothing is published.
    ///
    /// <paramref name="appliedAt"/> is the caller's single timestamp for the
    /// whole step, also sent to the operator journey as the push's
    /// <c>OccurredAt</c>. An item already <c>rejected</c> is an idempotent
    /// replay with no side effects.
    /// </summary>
    Task<WorkItemActionResult> RefuseAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        DateTimeOffset appliedAt,
        CancellationToken cancellationToken = default
    );
}
