using System.Globalization;
using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-495: the determination writes for a Standard-workflow type with nothing
/// bespoke: no accreditation number is minted (the type changes an
/// accreditation that already exists, or has none) and the payload is not
/// touched, except that a determination stores its AC6 effective date, when
/// it has one, on the top-level <c>effectiveDate</c>. Each step is one <see cref="IWorkItemPersistence.ReplaceAsync"/>
/// that re-checks the item first, retried on a concurrency conflict, then
/// fires the post-action hooks. The states, actions and audit entries are
/// re-accreditation's (RA-527), so the history reads the same:
/// <list type="bullet">
///   <item>Step 1: <c>action-applied</c> for <c>make-determination-*</c> and
///   <c>determination-made</c>.</item>
///   <item>Step 2: <c>action-applied</c> for <c>confirm-contact-*</c> and
///   <c>applicant-contacted</c>; an approval also <c>sla-clock-stopped</c>,
///   as re-accreditation's does.</item>
/// </list>
/// </summary>
internal sealed class StandardDeterminationWrites(
    IWorkItemPersistence persistence,
    IWorkItemRegistry registry,
    IEnumerable<IWorkItemPostActionHook> postActionHooks,
    ILogger<StandardDeterminationWrites> logger,
    TimeProvider? timeProvider = null
) : IStandardDeterminationSteps
{
    private const int MaxAttempts = 3;
    private const string ActionAppliedDisplayName = "Action applied";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IWorkItemPostActionHook[] _postActionHooks = postActionHooks.ToArray();

    /// <summary>Nothing to prepare: the approval goes straight to its step 1 write.</summary>
    public Task<StandardApprovalPreparation> PrepareApprovalAsync(
        WorkItem workItem,
        CancellationToken cancellationToken
    ) => Task.FromResult(StandardApprovalPreparation.Ready(RecordApprovedDeterminationAsync));

    private Task<WorkItemActionResult> RecordApprovedDeterminationAsync(
        Guid workItemId,
        DateOnly? effectiveDate,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    ) =>
        RecordDeterminationAsync(
            workItemId,
            user,
            new Determination(
                StandardWorkflow.MakeDeterminationApprovedActionId,
                StandardWorkflow.MakeDeterminationApprovedDisplayName,
                StandardWorkflow.ApprovedPendingContactStateId,
                StandardWorkflow.ApprovedStateId,
                "Determination made: Approved",
                StandardWorkflow.ApprovedStateId,
                effectiveDate
            ),
            cancellationToken
        );

    public Task<WorkItemActionResult> RecordRefusedDeterminationAsync(
        Guid workItemId,
        DateOnly? effectiveDate,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    ) =>
        RecordDeterminationAsync(
            workItemId,
            user,
            new Determination(
                StandardWorkflow.MakeDeterminationRejectedActionId,
                StandardWorkflow.MakeDeterminationRejectedDisplayName,
                StandardWorkflow.RejectedPendingContactStateId,
                StandardWorkflow.RejectedStateId,
                "Determination made: Refused",
                StandardWorkflow.RejectedStateId,
                effectiveDate
            ),
            cancellationToken
        );

    public WorkItemActionResult? CheckConfirmContact(
        Guid workItemId,
        WorkItem workItem,
        StandardConfirmContactStep step,
        ILogger? logger
    ) => null;

    public Task<WorkItemActionResult> ApproveAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        DateTimeOffset appliedAt,
        CancellationToken cancellationToken
    ) =>
        WriteAsync(
            workItemId,
            user,
            new StepTransition(
                StandardConfirmContactGuard.Approval.ActionId,
                StandardWorkflow.ConfirmContactDisplayName,
                StandardConfirmContactGuard.Approval.TerminalStateId
            ),
            workItem =>
                StandardConfirmContactGuard.Check(
                    workItemId,
                    workItem,
                    registry,
                    StandardConfirmContactGuard.Approval
                ),
            workItem =>
            {
                AppendAudit(
                    workItem,
                    "applicant-contacted",
                    "Applicant contacted",
                    user,
                    appliedAt.UtcDateTime,
                    new() { ["outcome"] = StandardWorkflow.ApprovedStateId }
                );
                AppendAudit(
                    workItem,
                    "sla-clock-stopped",
                    "SLA clock stopped",
                    user,
                    appliedAt.UtcDateTime,
                    new() { ["stoppedAt"] = appliedAt.ToString("O") }
                );
            },
            appliedAt,
            cancellationToken
        );

    public Task<WorkItemActionResult> RefuseAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        DateTimeOffset appliedAt,
        CancellationToken cancellationToken
    ) =>
        WriteAsync(
            workItemId,
            user,
            new StepTransition(
                StandardConfirmContactGuard.Refusal.ActionId,
                StandardWorkflow.ConfirmContactDisplayName,
                StandardConfirmContactGuard.Refusal.TerminalStateId
            ),
            workItem =>
                StandardConfirmContactGuard.Check(
                    workItemId,
                    workItem,
                    registry,
                    StandardConfirmContactGuard.Refusal
                ),
            workItem =>
                AppendAudit(
                    workItem,
                    "applicant-contacted",
                    "Applicant contacted",
                    user,
                    appliedAt.UtcDateTime,
                    new() { ["outcome"] = StandardWorkflow.RejectedStateId }
                ),
            appliedAt,
            cancellationToken
        );

    /// <summary>One outcome's step 1, with its AC6 effective date if the type takes one.</summary>
    private sealed record Determination(
        string ActionId,
        string ActionDisplayName,
        string PendingStateId,
        string FinalStateId,
        string AuditDisplayName,
        string Outcome,
        DateOnly? EffectiveDate
    );

    /// <summary>The transition a write applies: its action, that action's display name and the state it lands in.</summary>
    private readonly record struct StepTransition(string ActionId, string ActionDisplayName, string ToStateId);

    private Task<WorkItemActionResult> RecordDeterminationAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        Determination determination,
        CancellationToken cancellationToken
    )
    {
        var now = _timeProvider.GetUtcNow();
        return WriteAsync(
            workItemId,
            user,
            new StepTransition(determination.ActionId, determination.ActionDisplayName, determination.PendingStateId),
            workItem => CheckStepOne(workItemId, workItem, determination),
            workItem =>
            {
                var details = new Dictionary<string, string?> { ["outcome"] = determination.Outcome };
                if (determination.EffectiveDate is { } effectiveDate)
                {
                    var stamped = effectiveDate.ToString(StandardEffectiveDate.DateFormat, CultureInfo.InvariantCulture);
                    var payload = (workItem.Payload ?? new BsonDocument()).DeepClone().AsBsonDocument;
                    payload[StandardEffectiveDate.Field] = stamped;
                    workItem.ReplacePayload(payload);
                    details[StandardEffectiveDate.Field] = stamped;
                }

                AppendAudit(
                    workItem,
                    "determination-made",
                    determination.AuditDisplayName,
                    user,
                    now.UtcDateTime,
                    details
                );
            },
            now,
            cancellationToken
        );
    }

    /// <summary>
    /// Step 1's checks, against the item as it is on each attempt. The same
    /// outcome already recorded (pending or final) is a replay; the other
    /// outcome's final state or withdrawn is terminal; otherwise the item's
    /// own rules must declare the step 1 transition from where it is.
    /// </summary>
    private WorkItemActionResult? CheckStepOne(
        Guid workItemId,
        WorkItem? workItem,
        Determination determination
    )
    {
        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."
            );
        }

        if (Is(workItem.StateId, determination.PendingStateId) || Is(workItem.StateId, determination.FinalStateId))
        {
            return WorkItemActionResult.IdempotentReplay(workItem);
        }

        if (
            Is(workItem.StateId, StandardWorkflow.ApprovedStateId)
            || Is(workItem.StateId, StandardWorkflow.RejectedStateId)
            || Is(workItem.StateId, StandardWorkflow.WithdrawnStateId)
        )
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.TerminalState,
                $"Work item {workItemId} is in terminal state '{workItem.StateId}'; no actions are allowed."
            );
        }

        var template = WorkItemEngineRules.ResolveTemplate(workItem, registry);
        var declared =
            template?.Transitions.Any(t =>
                Is(t.ActionId, determination.ActionId) && Is(t.FromStateId, workItem.StateId)
            ) == true;
        return declared
            ? null
            : WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Action '{determination.ActionId}' is not available for work item {workItemId} "
                    + $"in state '{workItem.StateId}' (template '{workItem.TemplateVersion}')."
            );
    }

    private async Task<WorkItemActionResult> WriteAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        StepTransition transition,
        Func<WorkItem?, WorkItemActionResult?> guard,
        Action<WorkItem> applyStep,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(user);

        if (StandardLifecycleAudit.RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);
            if (guard(workItem) is { } stop)
            {
                return stop;
            }

            var previousState = workItem!.StateId;
            workItem.StateId = transition.ToStateId;
            workItem.LastModifiedAt = now.UtcDateTime;

            AppendAudit(
                workItem,
                "action-applied",
                ActionAppliedDisplayName,
                user,
                now.UtcDateTime,
                new()
                {
                    ["actionId"] = transition.ActionId,
                    ["actionDisplayName"] = transition.ActionDisplayName,
                    ["fromStateId"] = previousState,
                    ["toStateId"] = workItem.StateId,
                }
            );
            applyStep(workItem);

            try
            {
                await persistence.ReplaceAsync(workItem, cancellationToken);
            }
            catch (WorkItemConcurrencyException) when (attempt < MaxAttempts)
            {
                continue;
            }
            catch (WorkItemConcurrencyException ex)
            {
                logger.LogError(
                    ex,
                    "Determination step {ActionId} for work item {WorkItemId} abandoned after {Attempts} "
                        + "attempts due to repeated concurrency conflicts.",
                    transition.ActionId,
                    workItemId,
                    MaxAttempts
                );
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.ConcurrencyConflict,
                    $"Work item '{workItemId}' was modified concurrently. Reload the work item and retry."
                );
            }

            logger.LogInformation(
                "Work item {WorkItemId} ({TypeId}) moved to '{StateId}' ({ActionId}) by {UserId}",
                workItem.Id,
                workItem.TypeId,
                workItem.StateId,
                transition.ActionId,
                user.FindFirstValue("user:id")
            );

            await StandardLifecycleAudit.InvokeActionAppliedHooksAsync(
                _postActionHooks,
                workItem,
                transition.ActionId,
                previousState,
                user,
                logger
            );
            return WorkItemActionResult.Success(workItem);
        }

        // Unreachable: the final attempt either returns success or the
        // ConcurrencyConflict failure above.
        throw new InvalidOperationException("Determination retry loop exited unexpectedly.");
    }

    private static void AppendAudit(
        WorkItem workItem,
        string action,
        string actionDisplayName,
        ClaimsPrincipal user,
        DateTime createdAt,
        Dictionary<string, string?> details
    ) =>
        StandardLifecycleAudit.Append(workItem, action, actionDisplayName, user, createdAt, details);

    private static bool Is(string? value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
