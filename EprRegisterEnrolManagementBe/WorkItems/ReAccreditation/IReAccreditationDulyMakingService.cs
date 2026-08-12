using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-316: module-scoped service that owns the bespoke duly-making workflow —
/// the <c>submitted → duly-made</c> transition plus the side effects the
/// generic engine must not perform (anchoring the SLA clock to the
/// regulator-entered payment date, stamping that date on the payload, and
/// firing the duly-made notification / operator status push).
///
/// Module-scoped by name per the framework's DI rule: modules never share
/// interfaces.
/// </summary>
internal interface IReAccreditationDulyMakingService
{
    /// <summary>
    /// Complete duly making for <paramref name="workItemId"/>.
    /// </summary>
    /// <param name="paymentDate">
    /// The already-validated date the operator paid. Validation lives in
    /// <see cref="ReAccreditationDulyMakingValidator"/> and runs at the
    /// endpoint, so this parameter is a parsed <see cref="DateOnly"/> — the
    /// service never sees a raw string.
    /// </param>
    Task<WorkItemActionResult> CompleteDulyMakingAsync(
        Guid workItemId,
        DateOnly paymentDate,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );
}
