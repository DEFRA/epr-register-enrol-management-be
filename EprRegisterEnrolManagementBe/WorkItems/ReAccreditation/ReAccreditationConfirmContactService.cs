using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-527 default <see cref="IReAccreditationConfirmContactService"/>. The
/// outcome is the one step 1 recorded, read off the item's pending-contact
/// state: <c>approved-pending-contact</c> → <c>approved</c>,
/// <c>rejected-pending-contact</c> → <c>rejected</c>. Step 2 does what the
/// old single-step approve or refuse did (RA-527 task 007).
///
/// Run order, agreed for step 2 and the same for both outcomes:
/// <list type="number">
///   <item><b>Every check that could refuse the write</b>, through
///   <see cref="ReAccreditationConfirmContactGuard"/>: pending state, the
///   item's rules, a readable payload and, for an approval, the step 1
///   number.</item>
///   <item><b>Pre-commit operator-journey push</b> (the decision push, with
///   its larger retry pipeline): to-state the terminal state, from-state the
///   pending state, one <c>OccurredAt</c>. If it cannot be delivered the step
///   is abandoned — UpstreamNotificationFailed, which the endpoint maps to a
///   500 — with no state, SLA, outcome audit, publishing or email change.
///   Only the <c>status-push-failed</c> entry ("Status failed to send to the
///   Registration &amp; Accreditation service") is recorded, as on main; it is
///   shown to regulators in the history. The regulator retries by confirming
///   again.</item>
///   <item><b>One write</b> through
///   <see cref="IReAccreditationApprovalService.ApproveAsync"/> (stops the SLA
///   clock at that same timestamp, stamps the accreditation start date and
///   year, and queues publishing) or
///   <see cref="IReAccreditationRefusalService.RefuseAsync"/> (state and audit
///   only: as the old single-step refuse, the SLA clock is not stopped).</item>
///   <item><b>Post-action hooks</b>, fired by that service after its write:
///   the Decision email for an approval, nothing for a refusal (RA-211).</item>
/// </list>
///
/// Because the checks run before the push, the operator is never told the
/// outcome of an item the write would then refuse. If the push is delivered
/// but the write then fails (repeated concurrency conflicts, or Mongo down),
/// a retry sends a new <c>OccurredAt</c>; the operator journey, already at
/// that terminal status, answers 200 with no change (RA-527 task 008, which
/// closed the gap where it answered 409), so the retry goes on to make the
/// write. An item whose write did land replays as success.
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
        if (
            ReAccreditationConfirmContactGuard.Check(workItemId, workItem, registry, logger: logger) is
            { } stop
        )
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
            // A retry pushes a new OccurredAt. Since RA-527 task 008 the operator
            // journey answers 200 with no change when it is already at that
            // terminal status (it used to answer 409), so the retry can make the
            // write. An operator journey without that change still refuses it.
            logger.LogWarning(
                "Confirm-contact for work item {WorkItemId} failed at the write ({FailureCode}) after the "
                    + "operator journey was notified; a retry is likely to be refused by the operator journey "
                    + "and the item may need manual intervention.",
                workItemId,
                result.FailureCode
            );
        }

        return result;
    }
}
