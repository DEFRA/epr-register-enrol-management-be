using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-410 default <see cref="IReAccreditationLogDecisionService"/>, behind
/// <c>POST /work-items/re-accreditation/{id}/decision</c>.
///
/// RA-495: the determination is the shared
/// <see cref="StandardLogDecisionService"/>, here limited to re-accreditation
/// (any other type is refused as before). Re-accreditation's own writes are
/// its approval and refusal services: approving mints the accreditation number
/// first and stamps it (step 1, <c>approved-pending-contact</c>); refusing
/// mints nothing (<c>rejected-pending-contact</c>). Nothing is pushed and no
/// email is sent until confirm contact (<see cref="IReAccreditationConfirmContactService"/>).
/// </summary>
internal sealed class ReAccreditationLogDecisionService(
    IWorkItemPersistence persistence,
    IWorkItemService engine,
    IReAccreditationApprovalService approvalService,
    IReAccreditationRefusalService refusalService,
    ILogger<ReAccreditationLogDecisionService> logger
) : IReAccreditationLogDecisionService
{
    private readonly StandardLogDecisionService _shared = new(
        persistence,
        engine,
        StandardLifecycleStrategies.Of(
            new ReAccreditationLifecycleStrategy(approvalService, refusalService, logger)
        ),
        logger
    );

    public Task<WorkItemActionResult> LogDecisionAsync(
        Guid workItemId,
        ReAccreditationDecisionOutcome outcome,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    ) =>
        _shared.LogDecisionAsync(
            workItemId,
            outcome == ReAccreditationDecisionOutcome.Approved
                ? StandardDecisionOutcome.Approved
                : StandardDecisionOutcome.Rejected,
            user,
            cancellationToken
        );
}
