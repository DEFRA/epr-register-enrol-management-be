using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-527: the rules for step 2 of a determination ("confirm applicant
/// contact"), for both outcomes, in one place.
/// <see cref="ReAccreditationConfirmContactService"/> applies them BEFORE the
/// operator-journey push, so the operator is never told "Granted" or
/// "Refused" about an item the write would then refuse, and the write itself
/// (<see cref="ReAccreditationApprovalService.ApproveAsync"/> or
/// <see cref="ReAccreditationRefusalService.RefuseAsync"/>) applies them again
/// on every attempt, against the item as it is then.
///
/// RA-495: the type-independent rules now live in
/// <see cref="StandardConfirmContactGuard"/>. This adds re-accreditation's
/// own: the type, a readable payload and, for an approval, the step 1
/// accreditation number (<see cref="CheckReAccreditationRules"/>, which is
/// also re-accreditation's
/// <see cref="IStandardDeterminationSteps.CheckConfirmContact"/>).
/// </summary>
internal static class ReAccreditationConfirmContactGuard
{
    public static readonly StandardConfirmContactStep Approval = StandardConfirmContactGuard.Approval;

    public static readonly StandardConfirmContactStep Refusal = StandardConfirmContactGuard.Refusal;

    /// <summary>The step that starts from <paramref name="stateId"/>, if it is a pending state.</summary>
    public static StandardConfirmContactStep? ForPendingState(string? stateId) =>
        StandardConfirmContactGuard.ForPendingState(stateId);

    /// <summary>
    /// Can step 2 apply the determination to <paramref name="workItem"/> now?
    /// Returns null when it can, or the result to hand straight back: a
    /// failure, or an idempotent replay for an item already in the step's
    /// terminal state (a retry, or a second click, after the first
    /// confirmation landed).
    ///
    /// <paramref name="step"/> names the outcome a write is about to apply;
    /// left null (the up-front check, before the push), it is read off the
    /// item's own state, and either terminal decision state is a replay.
    ///
    /// The item must be a re-accreditation, pass
    /// <see cref="StandardConfirmContactGuard.Check"/> and then
    /// <see cref="CheckReAccreditationRules"/>.
    /// </summary>
    public static WorkItemActionResult? Check(
        Guid workItemId,
        WorkItem? workItem,
        IWorkItemRegistry registry,
        StandardConfirmContactStep? step = null,
        ILogger? logger = null
    )
    {
        if (
            workItem is not null
            && !string.Equals(workItem.TypeId, ReAccreditationType.Id, StringComparison.OrdinalIgnoreCase)
        )
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                $"Work item {workItemId} is of type '{workItem.TypeId}', not '{ReAccreditationType.Id}'."
            );
        }

        if (StandardConfirmContactGuard.Check(workItemId, workItem, registry, step) is { } stop)
        {
            return stop;
        }

        return CheckReAccreditationRules(
            workItemId,
            workItem!,
            step ?? ForPendingState(workItem!.StateId)!,
            logger
        );
    }

    /// <summary>
    /// Re-accreditation's own step 2 rules, for an item that has passed
    /// <see cref="StandardConfirmContactGuard.Check"/>. Its payload must be
    /// readable (step 2 must never write over a payload it cannot read). An
    /// approval must also carry the real-format accreditation number step 1
    /// stamped: step 2 reuses that number and never mints one, so an item
    /// without it needs a human, not a new number.
    /// </summary>
    public static WorkItemActionResult? CheckReAccreditationRules(
        Guid workItemId,
        WorkItem workItem,
        StandardConfirmContactStep step,
        ILogger? logger
    )
    {
        // RA-527 task 007: step 2 writes the payload (an approval stamps the
        // start date and year), and a payload that cannot be read must never
        // be written over. Checked here, before the push, so the operator is
        // never told the outcome of an item the write would then refuse.
        if (ReAccreditationDecisionWrites.TryReadPayload(workItem, logger) is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item '{workItemId}' payload is corrupt and cannot be read. "
                    + "Inspect the server logs for details; a manual data repair may be required."
            );
        }

        if (step.Outcome != StandardDecisionOutcome.Approved)
        {
            return null;
        }

        var accreditationId = ReAccreditationAccreditationNumber.Read(workItem.Payload);
        if (!ReAccreditationAccreditationNumber.IsRealFormat(accreditationId))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item '{workItemId}' is in '{workItem.StateId}' but does not carry the "
                    + $"accreditation id step 1 issued (found '{accreditationId}'). "
                    + "Manual investigation required."
            );
        }

        return null;
    }
}
