using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-527 default <see cref="IReAccreditationConfirmContactService"/>. The
/// outcome is the one step 1 recorded, read off the item's pending-contact
/// state: <c>approved-pending-contact</c> → <c>approved</c>,
/// <c>rejected-pending-contact</c> → <c>rejected</c>.
///
/// Run order, agreed for step 2 and the same for both outcomes:
/// <list type="number">
///   <item><b>Pre-commit operator-journey push</b> (the decision push, with
///   its larger retry pipeline): to-state the terminal state, from-state the
///   pending state, one <c>OccurredAt</c>. If it cannot be delivered the step
///   is abandoned — UpstreamNotificationFailed, which the endpoint maps to a
///   500 — with nothing applied (only the support-facing
///   <c>status-push-failed</c> entry is recorded).</item>
///   <item><b>One write</b> through
///   <see cref="IReAccreditationApprovalService.ApproveAsync"/> or
///   <see cref="IReAccreditationRefusalService.RefuseAsync"/>, which stops the
///   SLA clock at that same timestamp (and, for an approval, queues
///   publishing).</item>
///   <item><b>Post-action hooks</b>, fired by that service after its write:
///   the Decision email for an approval, nothing for a refusal (RA-211).</item>
/// </list>
///
/// Every rule that could refuse the write is checked BEFORE the push, through
/// <see cref="ReAccreditationConfirmContactGuard"/>, so the operator is never
/// told the outcome of an item the write would then refuse. If the write
/// still fails after a delivered push (a concurrent change), the regulator
/// retries: the operator journey drops the duplicate push by its
/// <c>OccurredAt</c> ordering, and an item that did reach its terminal state
/// replays as success.
/// </summary>
internal sealed class ReAccreditationConfirmContactService(
    IWorkItemPersistence persistence,
    IWorkItemRegistry registry,
    IReAccreditationApprovalService approvalService,
    IReAccreditationRefusalService refusalService,
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
        if (ReAccreditationDecisionWrites.RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
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

        // The guard passed, so the item is in one of the two pending states.
        var step = ReAccreditationConfirmContactGuard.ForPendingState(workItem!.StateId)!;
        var appliedAt = _timeProvider.GetUtcNow();
        var delivery = await _decisionPush.PushAsync(
            workItem,
            step.PendingStateId,
            step.TerminalStateId,
            step.ActionId,
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

        var result =
            step.Outcome == ReAccreditationDecisionOutcome.Approved
                ? await approvalService.ApproveAsync(workItemId, user, appliedAt, cancellationToken)
                : await refusalService.RefuseAsync(workItemId, user, appliedAt, cancellationToken);

        if (result.IsSuccess && !result.IsIdempotentReplay)
        {
            await _decisionPush.RecordDeliveredAsync(delivery, user, cancellationToken);
        }
        else if (!result.IsSuccess)
        {
            // Known gap (RA-527 task 004): a retry pushes a new OccurredAt, and
            // an operator journey already at the terminal status answers 409,
            // which the push adapter treats as a failure.
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
