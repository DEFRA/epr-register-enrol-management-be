using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using static EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReAccreditationDecisionWrites;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-527 default <see cref="IReAccreditationRefusalService"/>: a refusal is
/// two steps, each one write.
///
/// <see cref="RecordRefusedDeterminationAsync"/> (step 1, the determination)
/// moves the item from <c>awaiting-decision</c> to the non-terminal
/// <c>rejected-pending-contact</c> and records the determination. Nothing is
/// applied.
///
/// <see cref="RefuseAsync"/> (step 2, "confirm applicant contact") applies
/// the outcome: it checks the item against
/// <see cref="ReAccreditationConfirmContactGuard"/>, stamps
/// <c>SlaClock.StoppedAt</c>, moves the item to <c>rejected</c> and appends
/// the action-applied, <c>applicant-contacted</c> and <c>sla-clock-stopped</c>
/// audit entries. Unlike an approval there is no accreditation number, no
/// publishing job and no email (RA-211: regulators send refusal notices
/// themselves).
///
/// After each write the registered <see cref="IWorkItemPostActionHook"/>s run
/// with the step's action id. On a <see cref="WorkItemConcurrencyException"/>
/// either write is retried by reloading the latest document and re-running
/// its guard, up to <see cref="MaxAttempts"/> times, as the approval service
/// does.
/// </summary>
internal sealed class ReAccreditationRefusalService(
    IWorkItemPersistence persistence,
    IWorkItemRegistry registry,
    IEnumerable<IWorkItemPostActionHook> postActionHooks,
    ILogger<ReAccreditationRefusalService> logger,
    TimeProvider? timeProvider = null
) : IReAccreditationRefusalService
{
    private const int MaxAttempts = 3;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IWorkItemPostActionHook[] _postActionHooks = postActionHooks.ToArray();

    public Task<WorkItemActionResult> RecordRefusedDeterminationAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    ) =>
        WriteAsync(
            workItemId,
            user,
            new WriteStep(
                ReAccreditationType.MakeDeterminationRejectedActionId,
                ReAccreditationType.MakeDeterminationRejectedDisplayName,
                ReAccreditationType.RejectedPendingContactStateId,
                StopsSlaClock: false
            ),
            workItem => ValidateStepOneTarget(workItemId, workItem),
            (workItem, now) =>
                AppendAudit(
                    workItem,
                    "determination-made",
                    "Determination made: Refused",
                    user,
                    now.UtcDateTime,
                    new() { ["outcome"] = ReAccreditationType.RejectedStateId }
                ),
            _timeProvider.GetUtcNow(),
            cancellationToken
        );

    public Task<WorkItemActionResult> RefuseAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        DateTimeOffset appliedAt,
        CancellationToken cancellationToken = default
    ) =>
        WriteAsync(
            workItemId,
            user,
            new WriteStep(
                ReAccreditationType.ConfirmContactRejectedActionId,
                ReAccreditationType.ConfirmContactDisplayName,
                ReAccreditationType.RejectedStateId,
                StopsSlaClock: true
            ),
            workItem =>
                ReAccreditationConfirmContactGuard.Check(
                    workItemId,
                    workItem,
                    registry,
                    ReAccreditationConfirmContactGuard.Refusal
                ),
            (workItem, now) =>
            {
                AppendAudit(
                    workItem,
                    "applicant-contacted",
                    "Applicant contacted",
                    user,
                    now.UtcDateTime,
                    new() { ["outcome"] = ReAccreditationType.RejectedStateId }
                );
                AppendAudit(
                    workItem,
                    "sla-clock-stopped",
                    "SLA clock stopped",
                    user,
                    now.UtcDateTime,
                    new() { ["stoppedAt"] = now.ToString("O") }
                );
            },
            appliedAt,
            cancellationToken
        );

    /// <summary>
    /// One step's write: the transition it applies, its target state, and
    /// whether it stops the payload's SLA clock (at the write's timestamp).
    /// </summary>
    private sealed record WriteStep(
        string ActionId,
        string ActionDisplayName,
        string ToStateId,
        bool StopsSlaClock
    );

    /// <summary>
    /// One step's write: load, guard, move to <see cref="WriteStep.ToStateId"/>
    /// (stopping the SLA clock at <paramref name="now"/> when the step says
    /// so), append the action-applied entry and then the step's own entries
    /// (<paramref name="appendStepAudit"/>), replace, and run the hooks —
    /// retried on a concurrency conflict.
    /// </summary>
    private async Task<WorkItemActionResult> WriteAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        WriteStep step,
        Func<WorkItem?, WorkItemActionResult?> guard,
        Action<WorkItem, DateTimeOffset> appendStepAudit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(user);

        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);

            // Re-checked on every attempt, against the item as it is now: a
            // concurrent write of the same step that landed first turns this
            // into a replay, and anything else that moved the item refuses it.
            if (guard(workItem) is { } stop)
            {
                return stop;
            }

            var previousState = workItem!.StateId;
            if (step.StopsSlaClock && TryStopSlaClock(workItem, now, logger) is null)
            {
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.InvalidTransition,
                    $"Work item '{workItemId}' payload is corrupt and cannot be read. "
                        + "Inspect the server logs for details; a manual data repair may be required."
                );
            }
            workItem.StateId = step.ToStateId;
            workItem.LastModifiedAt = now.UtcDateTime;

            AppendAudit(
                workItem,
                "action-applied",
                "Action applied",
                user,
                now.UtcDateTime,
                new()
                {
                    ["actionId"] = step.ActionId,
                    ["actionDisplayName"] = step.ActionDisplayName,
                    ["fromStateId"] = previousState,
                    ["toStateId"] = workItem.StateId,
                }
            );
            appendStepAudit(workItem, now);

            try
            {
                await persistence.ReplaceAsync(workItem, cancellationToken);
            }
            catch (WorkItemConcurrencyException) when (attempt < MaxAttempts)
            {
                continue;
            }
            catch (WorkItemConcurrencyException)
            {
                logger.LogError(
                    "Refusal ({ActionId}) for work item {WorkItemId} abandoned after {Attempts} attempts "
                        + "due to repeated concurrency conflicts.",
                    step.ActionId,
                    workItemId,
                    MaxAttempts
                );
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.ConcurrencyConflict,
                    $"Work item '{workItemId}' was modified concurrently. Reload the work item and retry."
                );
            }

            logger.LogInformation(
                "Re-accreditation work item {WorkItemId} moved to '{StateId}' ({ActionId}) by {UserId}",
                workItem.Id,
                workItem.StateId,
                step.ActionId,
                user.FindFirstValue("user:id")
            );

            await InvokeActionAppliedHooksAsync(
                _postActionHooks,
                workItem,
                step.ActionId,
                previousState,
                user,
                logger
            );
            return WorkItemActionResult.Success(workItem);
        }

        // Unreachable: the final attempt either returns success or the
        // ConcurrencyConflict failure above.
        throw new InvalidOperationException("Refusal retry loop exited unexpectedly.");
    }

    /// <summary>
    /// The guards for <see cref="RecordRefusedDeterminationAsync"/>. Returns a
    /// result to hand straight back (failure, or an idempotent replay for an
    /// item already pending contact) or null when the item may move. The step
    /// 1 transition is checked against the item's own frozen snapshot, so a
    /// case still on pre-v17 rules is refused until migrated.
    /// </summary>
    private WorkItemActionResult? ValidateStepOneTarget(Guid workItemId, WorkItem? workItem)
    {
        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."
            );
        }

        if (!Is(workItem.TypeId, ReAccreditationType.Id))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                $"Work item {workItemId} is of type '{workItem.TypeId}', not '{ReAccreditationType.Id}'."
            );
        }

        if (Is(workItem.StateId, ReAccreditationType.RejectedPendingContactStateId))
        {
            return WorkItemActionResult.IdempotentReplay(workItem);
        }

        if (
            Is(workItem.StateId, ReAccreditationType.ApprovedStateId)
            || Is(workItem.StateId, ReAccreditationType.RejectedStateId)
            || Is(workItem.StateId, ReAccreditationType.WithdrawnStateId)
        )
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.TerminalState,
                $"Work item {workItemId} is in terminal state '{workItem.StateId}'; no actions are allowed."
            );
        }

        var template = WorkItemEngineRules.ResolveTemplate(workItem, registry);
        var declared = template?.Transitions.Any(t =>
            Is(t.ActionId, ReAccreditationType.MakeDeterminationRejectedActionId)
            && Is(t.FromStateId, workItem.StateId)
        );
        if (declared != true)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Action '{ReAccreditationType.MakeDeterminationRejectedActionId}' is not available for "
                    + $"work item {workItemId} in state '{workItem.StateId}' "
                    + $"(template '{workItem.TemplateVersion}')."
            );
        }

        return null;
    }

    private static bool Is(string? value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
