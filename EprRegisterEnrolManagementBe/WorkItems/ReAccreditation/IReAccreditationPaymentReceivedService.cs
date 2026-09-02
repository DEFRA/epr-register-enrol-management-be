using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-523 module-scoped service that carries a re-accreditation work item
/// FORWARD out of the non-terminal <c>updated</c> waypoint into assessment,
/// for the <c>duly-made</c> origin only. Module DI uses module-scoped
/// interfaces so the re-accreditation folder stays self-contained (mirrors
/// <see cref="IReAccreditationContinueReviewService"/>).
/// </summary>
internal interface IReAccreditationPaymentReceivedService
{
    /// <summary>
    /// Move a work item that was queried after being duly made out of
    /// <c>updated</c> and on to <c>assessment-in-progress</c> — where
    /// <c>payment-received</c> would have taken it had the query never been
    /// raised — instead of returning it to <c>duly-made</c>.
    ///
    /// The caller never supplies an action id, and never supplies an origin:
    /// both are resolved from the work item's own audit history. The
    /// originating state MUST resolve to <c>duly-made</c>; every other origin
    /// is refused, so an application queried out of <c>submitted</c> still has
    /// to go through duly making (which is what anchors the SLA clock).
    ///
    /// Idempotent: a work item that has already reached
    /// <c>assessment-in-progress</c> (a duplicate/retried call, e.g. a
    /// double-click) succeeds as an
    /// <see cref="WorkItemActionResult.IsIdempotentReplay">idempotent
    /// replay</see> rather than failing.
    /// </summary>
    Task<WorkItemActionResult> RecordPaymentReceivedAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);
}
