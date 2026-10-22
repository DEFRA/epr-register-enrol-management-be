using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-527: step 2 of the two-step determination — the regulator confirms they
/// have contacted the applicant, and only then is the outcome applied.
///
/// Module-scoped by design, like <see cref="IReAccreditationLogDecisionService"/>:
/// the ordering (push before write) and the side effects are
/// re-accreditation's own business, not the framework's.
/// </summary>
internal interface IReAccreditationConfirmContactService
{
    /// <summary>
    /// Apply the determination recorded at step 1. From
    /// <c>approved-pending-contact</c>: push "Granted" to the operator journey
    /// first (on failure nothing is applied), then move the item to
    /// <c>approved</c> with the SLA clock stopped, an "Applicant contacted"
    /// audit entry, publishing queued and the Decision email sent.
    ///
    /// An item already <c>approved</c> is an idempotent replay with no side
    /// effects; any other state is a conflict.
    /// </summary>
    Task<WorkItemActionResult> ConfirmContactAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );
}
