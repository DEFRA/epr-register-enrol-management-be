using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-495: step 2 of a determination on a Standard-workflow work item — the
/// caseworker confirms they have contacted the applicant, and only then is
/// the outcome step 1 recorded applied.
/// </summary>
internal interface IStandardConfirmContactService
{
    /// <summary>
    /// From <c>approved-pending-contact</c> → <c>approved</c> (Granted); from
    /// <c>rejected-pending-contact</c> → <c>rejected</c>. An item already
    /// <c>approved</c> or <c>rejected</c> is an idempotent replay with no side
    /// effects; any other state is a conflict.
    /// </summary>
    Task<WorkItemActionResult> ConfirmContactAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// RA-495: confirm contact shared by every type that opts in to the
/// Standard-workflow lifecycle; re-accreditation's RA-527 service, moved here
/// unchanged except that the type check is now "has the type opted in", the
/// push goes to the type's own target and the write is the type's own.
///
/// Run order, the same for both outcomes:
/// <list type="number">
///   <item><b>Every check that could refuse the write</b>:
///   <see cref="StandardConfirmContactGuard"/>, then the type's own
///   (<see cref="IStandardDeterminationSteps.CheckConfirmContact"/>).</item>
///   <item><b>Pre-commit operator-journey push</b> through
///   <see cref="StandardDecisionPush"/> (the decision push, with its larger
///   retry pipeline). If it cannot be delivered the step is abandoned —
///   UpstreamNotificationFailed, which the endpoint maps to a 500 — with
///   nothing written except the <c>status-push-failed</c> entry. The
///   caseworker retries by confirming again. A type with no push target is
///   not pushed.</item>
///   <item><b>One write</b> through the type's
///   <see cref="IStandardDeterminationSteps.ApproveAsync"/> or
///   <see cref="IStandardDeterminationSteps.RefuseAsync"/>, which fires the
///   post-action hooks.</item>
///   <item><b><c>status-push-sent</c></b> (or <c>-skipped</c>) once the write
///   has landed.</item>
/// </list>
///
/// If the push is delivered but the write then fails, a retry sends a new
/// <c>OccurredAt</c>; the operator journey, already at that terminal status,
/// answers 200 with no change, so the retry goes on to make the write.
/// </summary>
internal sealed class StandardConfirmContactService(
    IWorkItemPersistence persistence,
    IWorkItemRegistry registry,
    StandardLifecycleStrategies strategies,
    StandardDecisionPush decisionPush,
    ILogger logger,
    TimeProvider? timeProvider = null
) : IStandardConfirmContactService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<WorkItemActionResult> ConfirmContactAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(user);

        // Checked before anything else so a missing identity is a 401 before
        // the operator journey is told anything.
        if (StandardLifecycleAudit.RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);
        var strategy = workItem is null ? null : strategies.Find(workItem.TypeId);
        if (workItem is not null && strategy is null)
        {
            return strategies.NotOptedIn(workItemId, workItem.TypeId);
        }

        if (Check(workItemId, workItem, strategy) is { } stop)
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

        // The checks passed, so the item is in one of the two pending states.
        var step = StandardConfirmContactGuard.ForPendingState(workItem!.StateId)!;
        var appliedAt = _timeProvider.GetUtcNow();
        var delivery = await decisionPush.PushAsync(
            workItem,
            strategy!.Type,
            strategy.OperatorJourneyPush,
            step,
            StandardWorkflow.ConfirmContactDisplayName,
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
            step.Outcome == StandardDecisionOutcome.Approved
                ? await strategy.Determinations.ApproveAsync(workItemId, user, appliedAt, cancellationToken)
                : await strategy.Determinations.RefuseAsync(workItemId, user, appliedAt, cancellationToken);

        if (result.IsSuccess && !result.IsIdempotentReplay)
        {
            await decisionPush.RecordDeliveredAsync(delivery, user, cancellationToken);
        }
        else if (!result.IsSuccess)
        {
            logger.LogWarning(
                "Confirm-contact for work item {WorkItemId} failed at the write ({FailureCode}) after the "
                    + "operator journey was notified; a retry should complete the confirmation (the operator "
                    + "journey accepts a repeat of the same final status).",
                workItemId,
                result.FailureCode
            );
        }

        return result;
    }

    private WorkItemActionResult? Check(
        Guid workItemId,
        WorkItem? workItem,
        IStandardLifecycleStrategy? strategy
    )
    {
        if (StandardConfirmContactGuard.Check(workItemId, workItem, registry) is { } stop)
        {
            return stop;
        }

        var step = StandardConfirmContactGuard.ForPendingState(workItem!.StateId)!;
        return strategy!.Determinations.CheckConfirmContact(workItemId, workItem, step, logger);
    }
}
