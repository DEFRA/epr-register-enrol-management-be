using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Compound payment operation for a re-accreditation work item.
/// Stamps the SLA clock, transitions to <c>assessment-in-progress</c>,
/// unassigns, writes four operator-attributed audit entries, and triggers
/// the <c>AssessmentInProgress</c> Notify email — all in a single document
/// write.
/// </summary>
public interface IReAccreditationPaymentService
{
    /// <summary>
    /// Record a completed payment. Returns success when the operation
    /// committed; failure when validation fails or the item is not in the
    /// expected state.
    /// </summary>
    Task<WorkItemActionResult> RecordPaymentAsync(
        Guid workItemId,
        PaymentCompletedRequest request,
        CancellationToken cancellationToken = default);
}
