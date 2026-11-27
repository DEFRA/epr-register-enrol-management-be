using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-495 (from RA-527): one outcome's step 2 ("confirm applicant contact"):
/// the pending state it starts from, the terminal state it lands in and the
/// transition between them.
/// </summary>
internal sealed record StandardConfirmContactStep(
    StandardDecisionOutcome Outcome,
    string PendingStateId,
    string TerminalStateId,
    string ActionId
);

/// <summary>
/// RA-495 (from RA-527's re-accreditation guard): the type-independent rules
/// for step 2 of a determination, for both outcomes. Applied before the
/// operator-journey push, so the operator is never told "Granted" or
/// "Refused" about an item the write would then refuse, and again by the
/// write on every attempt, against the item as it is then. A type adds its own
/// checks through <see cref="IStandardDeterminationSteps.CheckConfirmContact"/>.
/// </summary>
internal static class StandardConfirmContactGuard
{
    public static readonly StandardConfirmContactStep Approval = new(
        StandardDecisionOutcome.Approved,
        StandardWorkflow.ApprovedPendingContactStateId,
        StandardWorkflow.ApprovedStateId,
        StandardWorkflow.ConfirmContactApprovedActionId
    );

    public static readonly StandardConfirmContactStep Refusal = new(
        StandardDecisionOutcome.Rejected,
        StandardWorkflow.RejectedPendingContactStateId,
        StandardWorkflow.RejectedStateId,
        StandardWorkflow.ConfirmContactRejectedActionId
    );

    private static readonly StandardConfirmContactStep[] s_steps = [Approval, Refusal];

    /// <summary>The step that starts from <paramref name="stateId"/>, if it is a pending state.</summary>
    public static StandardConfirmContactStep? ForPendingState(string? stateId) =>
        s_steps.FirstOrDefault(s => Is(stateId, s.PendingStateId));

    /// <summary>
    /// Can step 2 apply the determination to <paramref name="workItem"/> now?
    /// Returns null when it can, or the result to hand straight back: a
    /// failure, or an idempotent replay for an item already in the step's
    /// terminal state.
    ///
    /// <paramref name="step"/> names the outcome a write is about to apply;
    /// left null (the up-front check, before the push), it is read off the
    /// item's own state, and either terminal decision state is a replay.
    ///
    /// The item must be in the step's pending state, and its own frozen
    /// snapshot (or, for a legacy item with none, the registered type) must
    /// declare the confirm-contact transition. The caller has already checked
    /// the item's type has opted in.
    /// </summary>
    public static WorkItemActionResult? Check(
        Guid workItemId,
        WorkItem? workItem,
        IWorkItemRegistry registry,
        StandardConfirmContactStep? step = null
    )
    {
        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."
            );
        }

        var replayStates = step is null ? s_steps : [step];
        if (replayStates.Any(s => Is(workItem.StateId, s.TerminalStateId)))
        {
            return WorkItemActionResult.IdempotentReplay(workItem);
        }

        if (StandardWorkflow.IsFinalState(workItem.StateId))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.TerminalState,
                $"Work item {workItemId} is in terminal state '{workItem.StateId}'; no actions are allowed."
            );
        }

        step ??= ForPendingState(workItem.StateId);
        if (step is null || !Is(workItem.StateId, step.PendingStateId))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Applicant contact can only be confirmed for a work item in "
                    + $"'{step?.PendingStateId ?? string.Join("' or '", s_steps.Select(s => s.PendingStateId))}', "
                    + $"but {workItemId} is in '{workItem.StateId}'."
            );
        }

        var template = WorkItemEngineRules.ResolveTemplate(workItem, registry);
        var declared =
            template?.Transitions.Any(t =>
                Is(t.ActionId, step.ActionId) && Is(t.FromStateId, workItem.StateId)
            ) == true;
        if (!declared)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Action '{step.ActionId}' is not available for "
                    + $"work item {workItemId} (template '{workItem.TemplateVersion}')."
            );
        }

        return null;
    }

    private static bool Is(string? value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
