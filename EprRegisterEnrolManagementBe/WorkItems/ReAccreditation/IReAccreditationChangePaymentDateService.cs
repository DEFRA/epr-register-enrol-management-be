using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-576: module-scoped service that corrects the payment date recorded when a
/// re-accreditation was duly made.
///
/// Module-scoped by name per the framework's DI rule: modules never share
/// interfaces.
/// </summary>
internal interface IReAccreditationChangePaymentDateService
{
    /// <summary>
    /// Replace <c>payload.paymentDate</c> on <paramref name="workItemId"/> and
    /// record who changed it, from what, to what and why. The work item's state
    /// and SLA clock are deliberately left untouched.
    /// </summary>
    /// <param name="paymentDate">The already-validated corrected date.</param>
    /// <param name="reason">The already-validated, trimmed reason.</param>
    Task<WorkItemActionResult> ChangePaymentDateAsync(
        Guid workItemId,
        DateOnly paymentDate,
        string reason,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );
}
