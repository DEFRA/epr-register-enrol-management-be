using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-252 module-scoped service that withdraws a re-accreditation
/// application on the operator's behalf. Module DI uses module-scoped
/// interfaces so the re-accreditation folder stays self-contained (mirrors
/// <see cref="IReAccreditationQueryService"/>).
/// </summary>
internal interface IReAccreditationWithdrawService
{
    /// <summary>
    /// Record the operator's withdrawal reason as a work item note, then
    /// move the work item to <c>withdrawn</c>. The caller never supplies an
    /// action id: the correct <c>withdraw</c>/<c>withdraw-during-*</c>
    /// transition is resolved from the work item's own current state, since
    /// the operator backend does not track case-working's finer-grained
    /// state machine.
    /// </summary>
    Task<WorkItemActionResult> WithdrawAsync(
        Guid workItemId,
        string reason,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );
}
