using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-527 default <see cref="IReAccreditationConfirmContactService"/>.
///
/// Run order, agreed for step 2:
/// <list type="number">
///   <item><b>Pre-commit operator-journey push</b> (the decision push, with
///   its larger retry pipeline): to-state <c>approved</c>, from-state
///   <c>approved-pending-contact</c>, one <c>OccurredAt</c>. If it cannot be
///   delivered the step is abandoned — UpstreamNotificationFailed, which the
///   endpoint maps to a 500 — with nothing applied (only the support-facing
///   <c>status-push-failed</c> entry is recorded, as for a refusal).</item>
///   <item><b>One write</b> through
///   <see cref="IReAccreditationApprovalService.ApproveAsync"/>, which stops
///   the SLA clock at that same timestamp and queues publishing.</item>
///   <item><b>Post-action hooks</b> (the Decision email), fired by the
///   approval service after its write.</item>
/// </list>
///
/// Every rule that could refuse the write is checked BEFORE the push, through
/// <see cref="ReAccreditationConfirmContactGuard"/>, so the operator is never
/// told "Granted" about an item the write would then refuse. If the write
/// still fails after a delivered push (a concurrent change), the regulator
/// retries: the operator journey drops the duplicate push by its
/// <c>OccurredAt</c> ordering, and an item that did reach <c>approved</c>
/// replays as success.
/// </summary>
internal sealed class ReAccreditationConfirmContactService(
    IWorkItemPersistence persistence,
    IWorkItemRegistry registry,
    IReAccreditationApprovalService approvalService,
    IOperatorBackendPushAdapter pushAdapter,
    IWorkItemAuditAppender auditAppender,
    ILogger<ReAccreditationConfirmContactService> logger,
    TimeProvider? timeProvider = null
) : IReAccreditationConfirmContactService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ReAccreditationDecisionPush _decisionPush = new(
        pushAdapter,
        auditAppender,
        logger
    );

    public async Task<WorkItemActionResult> ConfirmContactAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(user);

        // Checked before anything else so a missing identity is a 401 before
        // the operator journey is told anything.
        if (string.IsNullOrWhiteSpace(user.FindFirstValue("user:id")))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.MissingActorIdentity,
                "Mutating this work item requires an authenticated end user; "
                    + "the request did not include a 'user:id' claim."
            );
        }

        var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);
        if (ReAccreditationConfirmContactGuard.Check(workItemId, workItem, registry) is { } stop)
        {
            if (stop.IsIdempotentReplay)
            {
                logger.LogInformation(
                    "Confirm-contact for work item {WorkItemId} is a no-op: already '{StateId}'.",
                    workItemId,
                    workItem!.StateId
                );
            }
            return stop;
        }

        var appliedAt = _timeProvider.GetUtcNow();
        var delivery = await _decisionPush.PushAsync(
            workItem!,
            ReAccreditationType.ApprovedPendingContactStateId,
            ReAccreditationType.ApprovedStateId,
            ReAccreditationType.ConfirmContactApprovedActionId,
            ReAccreditationType.ConfirmContactDisplayName,
            appliedAt.UtcDateTime,
            user,
            cancellationToken
        );
        if (delivery.Failed)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UpstreamNotificationFailed,
                "Applicant contact could not be confirmed because the operator journey could not be "
                    + "notified. No change was made; try again shortly."
            );
        }

        var result = await approvalService.ApproveAsync(
            workItemId,
            user,
            appliedAt,
            cancellationToken
        );

        if (result.IsSuccess && !result.IsIdempotentReplay)
        {
            await _decisionPush.RecordDeliveredAsync(delivery, user, cancellationToken);
        }
        else if (!result.IsSuccess)
        {
            logger.LogWarning(
                "Confirm-contact for work item {WorkItemId} failed at the write ({FailureCode}) after the "
                    + "operator journey was notified; the call may be safely retried.",
                workItemId,
                result.FailureCode
            );
        }

        return result;
    }
}
