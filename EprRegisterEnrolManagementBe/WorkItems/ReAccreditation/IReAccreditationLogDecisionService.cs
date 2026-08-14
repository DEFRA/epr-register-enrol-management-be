using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-410: records a final determination on a re-accreditation application in
/// a single caller-visible operation.
///
/// Module-scoped by design (see the framework's "module DI uses module-scoped
/// interfaces" rule): the ordering, the intermediate state and the approval
/// side effects are all re-accreditation's own business, not the framework's.
/// </summary>
internal interface IReAccreditationLogDecisionService
{
    /// <summary>
    /// Move a work item from <c>assessment-in-progress</c> through
    /// <c>awaiting-decision</c> to its terminal state, applying both hops
    /// server-side so the caller makes exactly one call.
    ///
    /// Also accepts a work item already sitting in <c>awaiting-decision</c>,
    /// which is what makes the operation resumable: if the process dies
    /// between the two hops, replaying the identical call finishes the job
    /// rather than leaving the application parked in an intermediate state
    /// with no way forward.
    /// </summary>
    Task<WorkItemActionResult> LogDecisionAsync(
        Guid workItemId,
        ReAccreditationDecisionOutcome outcome,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );
}
