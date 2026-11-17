using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-527 default <see cref="IReAccreditationConfirmContactService"/>, behind
/// <c>POST /work-items/re-accreditation/{id}/confirm-contact</c>.
///
/// RA-495: confirm contact is the shared
/// <see cref="StandardConfirmContactService"/>, here limited to
/// re-accreditation (any other type is refused as before): checks
/// (<see cref="ReAccreditationConfirmContactGuard"/>), then the pre-commit
/// push to the accreditation-application status route (a failure records
/// <c>status-push-failed</c> and commits nothing), then one write through
/// <see cref="IReAccreditationApprovalService.ApproveAsync"/> or
/// <see cref="IReAccreditationRefusalService.RefuseAsync"/>, then
/// <c>status-push-sent</c>.
/// </summary>
internal sealed class ReAccreditationConfirmContactService(
    IWorkItemPersistence persistence,
    IWorkItemRegistry registry,
    IReAccreditationApprovalService approvalService,
    IReAccreditationRefusalService refusalService,
    ReAccreditationDecisionPush decisionPush,
    ILogger<ReAccreditationConfirmContactService> logger,
    TimeProvider? timeProvider = null
) : IReAccreditationConfirmContactService
{
    private readonly StandardConfirmContactService _shared = new(
        persistence,
        registry,
        StandardLifecycleStrategies.Of(
            new ReAccreditationLifecycleStrategy(approvalService, refusalService, logger)
        ),
        decisionPush,
        logger,
        timeProvider
    );

    public Task<WorkItemActionResult> ConfirmContactAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    ) => _shared.ConfirmContactAsync(workItemId, user, cancellationToken);
}
