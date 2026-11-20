using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-495: records a final determination (step 1 of two) on a
/// Standard-workflow work item in one caller-visible operation.
/// </summary>
internal interface IStandardLogDecisionService
{
    /// <summary>
    /// Move a work item from <c>assessment-started</c> or
    /// <c>assessment-in-progress</c> through <c>awaiting-decision</c> to the
    /// non-terminal pending-contact state for <paramref name="outcome"/>,
    /// applying both hops server-side. Also accepts an item already in
    /// <c>awaiting-decision</c>, so a call that failed between the hops is
    /// finished by replaying it.
    ///
    /// RA-495 AC6: <paramref name="effectiveDate"/> is the date an approval
    /// takes effect from, required (and checked, <see cref="StandardEffectiveDate"/>)
    /// when approving a type that
    /// <see cref="IStandardLifecycleStrategy.TakesEffectiveDate"/>. A refusal of
    /// such a type records the submission date instead; any other call ignores it.
    /// </summary>
    Task<WorkItemActionResult> LogDecisionAsync(
        Guid workItemId,
        StandardDecisionOutcome outcome,
        DateOnly? effectiveDate,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );

    /// <summary>A determination with no effective date, as every type but add-ORS records.</summary>
    Task<WorkItemActionResult> LogDecisionAsync(
        Guid workItemId,
        StandardDecisionOutcome outcome,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    ) => LogDecisionAsync(workItemId, outcome, effectiveDate: null, user, cancellationToken);
}

/// <summary>
/// RA-495: make determination shared by every type that opts in to the
/// Standard-workflow lifecycle; re-accreditation's RA-410 / RA-527 service,
/// moved here unchanged except that the type check is now "has the type
/// opted in" and the writes (including re-accreditation's accreditation
/// number minting) are the type's own
/// (<see cref="IStandardLifecycleStrategy.Determinations"/>).
///
/// RA-410: <c>submit-for-decision</c> and both step 1 transitions are
/// <see cref="WorkItemTransition.CallerInvocable"/> <c>false</c>, so this is
/// the only route to them. RA-527: a determination is step 1 of two. Nothing
/// is applied: no operator-journey push, no SLA clock stop and no email;
/// those belong to confirm contact (<see cref="IStandardConfirmContactService"/>),
/// so the operator never learns the outcome before the regulator has
/// contacted them.
/// </summary>
internal sealed class StandardLogDecisionService(
    IWorkItemPersistence persistence,
    IWorkItemService engine,
    IWorkItemRegistry registry,
    StandardLifecycleStrategies strategies,
    ILogger logger
) : IStandardLogDecisionService
{
    private const string AssessmentStateId = "assessment-in-progress";
    private const string AwaitingDecisionStateId = "awaiting-decision";
    private const string SubmitForDecisionActionId = "submit-for-decision";

    public async Task<WorkItemActionResult> LogDecisionAsync(
        Guid workItemId,
        StandardDecisionOutcome outcome,
        DateOnly? effectiveDate,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(user);

        // Checked here as well as by the engine so the caller gets a 401 for a
        // missing identity before any state is touched, rather than after the
        // first of the two hops has already landed.
        if (StandardLifecycleAudit.RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);

        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."
            );
        }

        if (strategies.Find(workItem.TypeId) is not { } strategy)
        {
            return strategies.NotOptedIn(workItemId, workItem.TypeId);
        }

        var isApproval = outcome == StandardDecisionOutcome.Approved;

        // RA-527: a determination is step 1 of two — it lands in the
        // non-terminal pending-contact state for its outcome.
        var targetStateId = isApproval
            ? StandardWorkflow.ApprovedPendingContactStateId
            : StandardWorkflow.RejectedPendingContactStateId;
        var finalStateId = isApproval ? StandardWorkflow.ApprovedStateId : StandardWorkflow.RejectedStateId;

        var entry = CheckEntryState(workItem, targetStateId, finalStateId);
        if (entry is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"A decision can only be recorded for a work item in '{StandardWorkflow.AssessmentStartedStateId}', "
                    + $"'{AssessmentStateId}' or '{AwaitingDecisionStateId}', but {workItemId} is in "
                    + $"'{workItem.StateId}'."
            );
        }

        if (entry.Value.Result is { } entryResult)
        {
            return entryResult;
        }

        var needsSubmitForDecision = entry.Value.NeedsSubmitForDecision;

        // RA-495 AC6: checked before anything is written, like the template
        // below. Only a type that takes one records a date: an approval's is
        // the one entered; a refusal's is the submission date.
        var (dateFailure, determinationEffectiveDate) = ResolveEffectiveDate(
            strategy,
            workItem,
            isApproval,
            effectiveDate
        );
        if (dateFailure is not null)
        {
            return dateFailure;
        }

        // RA-527: checked here, before any mint and the hop, so an item still
        // on older rules (its snapshot not yet migrated) is refused with
        // nothing written rather than parked in 'awaiting-decision' first.
        var stepOneActionId = isApproval
            ? StandardWorkflow.MakeDeterminationApprovedActionId
            : StandardWorkflow.MakeDeterminationRejectedActionId;
        // Resolved as the engine resolves it (frozen snapshot, else the live
        // registered type). No template at all (an unregistered type with no
        // snapshot) declares nothing, so it is refused the same way, as the
        // sibling Standard* services refuse it.
        var template = WorkItemEngineRules.ResolveTemplate(workItem, registry);
        if (template?.Transitions.Any(t => Is(t.ActionId, stepOneActionId)) != true)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item {workItemId} is on template '{workItem.TemplateVersion}', which does not "
                    + $"declare '{stepOneActionId}'."
            );
        }

        return isApproval
            ? await RecordApprovalAsync(
                strategy.Determinations,
                workItem,
                determinationEffectiveDate,
                needsSubmitForDecision,
                user,
                cancellationToken
            )
            : await RecordRefusalAsync(
                strategy.Determinations,
                workItemId,
                determinationEffectiveDate,
                needsSubmitForDecision,
                user,
                cancellationToken
            );
    }

    /// <summary>
    /// What <see cref="CheckEntryState"/> decided: a result to hand straight
    /// back (an idempotent replay or a terminal-state refusal), or, when the
    /// call may proceed, whether the <c>submit-for-decision</c> hop is still
    /// needed.
    /// </summary>
    private readonly record struct EntryCheck(WorkItemActionResult? Result, bool NeedsSubmitForDecision);

    /// <summary>
    /// The entry-state checks, in order. Returns null for a state a decision
    /// cannot be recorded from (the caller words that refusal).
    ///
    /// A double-click, or a client retrying a response it never received,
    /// must not fail: only a replay of the SAME outcome is a no-op (on its
    /// pending-contact or its final state); an item already decided the other
    /// way is a genuine conflict. The accepted entry states are
    /// 'assessment-started' and 'assessment-in-progress' (both take the
    /// submit-for-decision hop first) and 'awaiting-decision' (so an
    /// application stranded mid-hop is finished by the identical call).
    /// </summary>
    private EntryCheck? CheckEntryState(WorkItem workItem, string targetStateId, string finalStateId)
    {
        if (Is(workItem.StateId, targetStateId) || Is(workItem.StateId, finalStateId))
        {
            logger.LogInformation(
                "Log-decision for work item {WorkItemId} is a no-op: already in state '{StateId}'.",
                workItem.Id,
                workItem.StateId
            );
            return new EntryCheck(WorkItemActionResult.IdempotentReplay(workItem), false);
        }

        // Enumerated rather than read off the template: a decision must be
        // refused on a closed application even when its frozen snapshot is too
        // old to carry terminal metadata.
        if (
            Is(workItem.StateId, StandardWorkflow.ApprovedStateId)
            || Is(workItem.StateId, StandardWorkflow.RejectedStateId)
            || Is(workItem.StateId, StandardWorkflow.WithdrawnStateId)
        )
        {
            return new EntryCheck(
                WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.TerminalState,
                    $"Work item {workItem.Id} is in terminal state '{workItem.StateId}'; no decision can be recorded."
                ),
                false
            );
        }

        var needsSubmitForDecision =
            Is(workItem.StateId, AssessmentStateId)
            || Is(workItem.StateId, StandardWorkflow.AssessmentStartedStateId);

        return needsSubmitForDecision || Is(workItem.StateId, AwaitingDecisionStateId)
            ? new EntryCheck(null, needsSubmitForDecision)
            : null;
    }

    /// <summary>
    /// RA-495 AC6: the effective date to record. Only a type that takes one
    /// records a date: an approval's is the one entered (checked here); a
    /// refusal's is the submission date. Returns a failure to hand back, or
    /// the date (null for a type that takes none).
    /// </summary>
    private static (WorkItemActionResult? Failure, DateOnly? EffectiveDate) ResolveEffectiveDate(
        IStandardLifecycleStrategy strategy,
        WorkItem workItem,
        bool isApproval,
        DateOnly? enteredDate
    )
    {
        if (!strategy.TakesEffectiveDate)
        {
            return (null, null);
        }

        if (!isApproval)
        {
            return (null, StandardEffectiveDate.ForRefusal(workItem));
        }

        return StandardEffectiveDate.Check(workItem, enteredDate) is { } dateFailure
            ? (dateFailure, null)
            : (null, enteredDate);
    }

    /// <summary>
    /// Step 1 of an approval. The type prepares first (re-accreditation
    /// refuses an item that already carries a number, then mints one), so a
    /// failure there leaves the item untouched; then the
    /// <c>submit-for-decision</c> hop if needed; then the type's write.
    /// </summary>
    private async Task<WorkItemActionResult> RecordApprovalAsync(
        IStandardDeterminationSteps determinations,
        WorkItem workItem,
        DateOnly? effectiveDate,
        bool needsSubmitForDecision,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var workItemId = workItem.Id;
        var preparation = await determinations.PrepareApprovalAsync(workItem, cancellationToken);
        if (preparation.Refusal is { } refused)
        {
            return refused;
        }

        if (
            needsSubmitForDecision
            && await ApplySubmitForDecisionAsync(workItemId, user, cancellationToken) is { } failed
        )
        {
            return failed;
        }

        var result = await preparation.RecordApprovedDeterminationAsync(
            workItemId,
            effectiveDate,
            user,
            cancellationToken
        );

        LogStepOne(result, workItemId, "approved", user);
        return result;
    }

    /// <summary>
    /// Step 1 of a refusal: the <c>submit-for-decision</c> hop if needed, then
    /// the type's write. Nothing is minted.
    /// </summary>
    private async Task<WorkItemActionResult> RecordRefusalAsync(
        IStandardDeterminationSteps determinations,
        Guid workItemId,
        DateOnly? effectiveDate,
        bool needsSubmitForDecision,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (
            needsSubmitForDecision
            && await ApplySubmitForDecisionAsync(workItemId, user, cancellationToken) is { } failed
        )
        {
            return failed;
        }

        var result = await determinations.RecordRefusedDeterminationAsync(
            workItemId,
            effectiveDate,
            user,
            cancellationToken
        );

        LogStepOne(result, workItemId, "refused", user);
        return result;
    }

    private void LogStepOne(WorkItemActionResult result, Guid workItemId, string outcome, ClaimsPrincipal user)
    {
        if (result.IsSuccess)
        {
            logger.LogInformation(
                "Work item {WorkItemId} determined as {Outcome} by {UserId}; pending applicant contact.",
                workItemId,
                outcome,
                user.FindFirstValue("user:id")
            );
            return;
        }

        // If the hop ran, the item sits in 'awaiting-decision', which this
        // service accepts as an entry state, so the identical call can simply
        // be retried.
        logger.LogWarning(
            "Log-decision for work item {WorkItemId} failed at the step 1 {Outcome} ({FailureCode}); "
                + "the call may be safely retried.",
            workItemId,
            outcome,
            result.FailureCode
        );
    }

    /// <summary>
    /// Applies the internal <c>submit-for-decision</c> hop. Returns the
    /// failure to hand back, or null when it landed.
    /// </summary>
    private async Task<WorkItemActionResult?> ApplySubmitForDecisionAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var submitted = await engine.ApplyActionAsync(
            workItemId,
            SubmitForDecisionActionId,
            user,
            cancellationToken
        );

        if (submitted.IsSuccess)
        {
            return null;
        }

        logger.LogWarning(
            "Log-decision for work item {WorkItemId} abandoned: could not apply "
                + "'{ActionId}' ({FailureCode}).",
            workItemId,
            SubmitForDecisionActionId,
            submitted.FailureCode
        );
        return submitted;
    }

    private static bool Is(string? value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
