using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-410 default <see cref="IReAccreditationLogDecisionService"/>.
///
/// Before RA-410 recording a determination took two caller round-trips:
/// <c>submit-for-decision</c> to park the application in
/// <c>awaiting-decision</c>, then <c>approve</c> / <c>reject</c> to close it.
/// A failure between the two left the application in <c>awaiting-decision</c>
/// with a checklist the caseworker had no CTA to discharge. This service
/// collapses both hops into one call so that window does not exist.
///
/// <c>awaiting-decision</c> itself survives — it is referenced across the
/// backend, the frontend and existing Mongo documents, and deleting it would
/// need a data migration for no user-visible gain. What is gone is any reason
/// for a human to see it.
///
/// Resolution strategy mirrors <see cref="ReAccreditationContinueReviewService"/>:
/// the caller names an outcome, never an action id. <c>submit-for-decision</c>
/// and both step 1 transitions are declared
/// <see cref="WorkItemTransition.CallerInvocable"/> <c>false</c>, so this
/// service is the only route to any of them.
///
/// RA-527: a determination is step 1 of two, for either outcome. Approving
/// mints the accreditation number and delegates to
/// <see cref="IReAccreditationApprovalService.RecordApprovedDeterminationAsync"/>,
/// which stamps it and moves the item to the non-terminal
/// <c>approved-pending-contact</c> state. Refusing mints nothing and delegates
/// to <see cref="IReAccreditationRefusalService.RecordRefusedDeterminationAsync"/>,
/// which moves the item to the non-terminal <c>rejected-pending-contact</c>
/// state. Either way the outcome is not applied: no Registration &amp;
/// Accreditation service push, no SLA clock stop, no publishing job and no
/// email — those belong to step 2 ("confirm applicant contact",
/// <see cref="IReAccreditationConfirmContactService"/>), so the operator never
/// learns the outcome before the regulator has contacted them. Because step 1
/// makes no push, the decision push and its pre-commit gate (epr-p86e) now
/// live in step 2 alone.
/// </summary>
internal sealed class ReAccreditationLogDecisionService(
    IWorkItemPersistence persistence,
    IWorkItemService engine,
    IReAccreditationApprovalService approvalService,
    IReAccreditationRefusalService refusalService,
    ILogger<ReAccreditationLogDecisionService> logger
) : IReAccreditationLogDecisionService
{
    private const string AssessmentStateId = "assessment-in-progress";
    private const string AwaitingDecisionStateId = "awaiting-decision";
    private const string SubmitForDecisionActionId = "submit-for-decision";
    private const string ApprovedStateId = ReAccreditationType.ApprovedStateId;
    private const string RejectedStateId = ReAccreditationType.RejectedStateId;
    private const string WithdrawnStateId = ReAccreditationType.WithdrawnStateId;

    private static readonly ReAccreditationType s_type = new();

    public async Task<WorkItemActionResult> LogDecisionAsync(
        Guid workItemId,
        ReAccreditationDecisionOutcome outcome,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(user);

        // Checked here as well as by the engine so the caller gets a 401 for a
        // missing identity before any state is touched, rather than after the
        // first of the two hops has already landed.
        if (ReAccreditationDecisionWrites.RequireActorIdentity(user) is { } identityFailure)
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

        if (
            !string.Equals(
                workItem.TypeId,
                ReAccreditationType.Id,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                $"Work item {workItemId} is of type '{workItem.TypeId}', not '{ReAccreditationType.Id}'."
            );
        }

        var isApproval = outcome == ReAccreditationDecisionOutcome.Approved;

        // RA-527: a determination is step 1 of two — it lands in the
        // non-terminal pending-contact state for its outcome.
        var targetStateId = isApproval
            ? ReAccreditationType.ApprovedPendingContactStateId
            : ReAccreditationType.RejectedPendingContactStateId;

        // A double-click, or a client retrying a response it never received,
        // must not fail. Only a replay of the SAME outcome is a no-op; an item
        // already decided the other way is a genuine conflict, because silently
        // succeeding would tell a caseworker their Refuse landed when the
        // application is in fact approved and an accreditation id is issued.
        if (string.Equals(workItem.StateId, targetStateId, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Log-decision for work item {WorkItemId} is a no-op: already in state '{StateId}'.",
                workItemId,
                workItem.StateId
            );
            return WorkItemActionResult.IdempotentReplay(workItem);
        }

        // Enumerated rather than read off the template, matching
        // ReAccreditationApprovalService: a decision must be refused on a
        // closed application even when its frozen snapshot is too old to
        // carry terminal metadata. RA-527: this includes a replay of the same
        // outcome on an application already Granted or Refused — the
        // determination was made once, and step 2 is what applied it.
        if (
            string.Equals(workItem.StateId, ApprovedStateId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(workItem.StateId, RejectedStateId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(workItem.StateId, WithdrawnStateId, StringComparison.OrdinalIgnoreCase)
        )
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.TerminalState,
                $"Work item {workItemId} is in terminal state '{workItem.StateId}'; no decision can be recorded."
            );
        }

        // The two accepted entry states. 'awaiting-decision' is accepted not
        // for the frontend's benefit — it never sends an application there —
        // but so that an application stranded mid-hop by an earlier failure,
        // or left there by the pre-RA-410 two-step flow, is finished by the
        // identical call rather than needing a bespoke rescue path. Anything
        // else — including RA-527's pending-contact states, whose
        // determination is already final — is a conflict.
        var needsSubmitForDecision = string.Equals(
            workItem.StateId,
            AssessmentStateId,
            StringComparison.OrdinalIgnoreCase
        );

        if (
            !needsSubmitForDecision
            && !string.Equals(
                workItem.StateId,
                AwaitingDecisionStateId,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"A decision can only be recorded for a work item in '{AssessmentStateId}' or "
                    + $"'{AwaitingDecisionStateId}', but {workItemId} is in '{workItem.StateId}'."
            );
        }

        // RA-527: checked here, before the mint and the hop, so an item still
        // on pre-v17 rules (its snapshot not yet migrated) is refused with
        // nothing written rather than parked in 'awaiting-decision' first.
        var stepOneActionId = isApproval
            ? ReAccreditationType.MakeDeterminationApprovedActionId
            : ReAccreditationType.MakeDeterminationRejectedActionId;
        if (!Declares(workItem, stepOneActionId))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item {workItemId} is on template '{workItem.TemplateVersion}', which does not "
                    + $"declare '{stepOneActionId}'."
            );
        }

        return isApproval
            ? await RecordApprovalAsync(workItemId, needsSubmitForDecision, user, cancellationToken)
            : await RecordRefusalAsync(workItemId, needsSubmitForDecision, user, cancellationToken);
    }

    /// <summary>
    /// RA-527 step 1 of an approval. Mints the accreditation number before
    /// anything is written (a failed mint leaves the item untouched), takes
    /// the <c>submit-for-decision</c> hop if needed, then has the approval
    /// service stamp the number and move the item to
    /// <c>approved-pending-contact</c>.
    ///
    /// Deliberately no operator-journey push: the operator must not learn of
    /// the outcome before the regulator has contacted them. The SLA clock is
    /// left running, nothing is published and no email is sent — all of that
    /// is step 2 ("confirm applicant contact").
    /// </summary>
    private async Task<WorkItemActionResult> RecordApprovalAsync(
        Guid workItemId,
        bool needsSubmitForDecision,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var correlationId = Guid.NewGuid();
        var accreditationNumber = await approvalService.ResolveAccreditationNumberAsync(
            workItemId,
            correlationId,
            cancellationToken
        );

        if (!accreditationNumber.IsSuccess)
        {
            logger.LogError(
                "Log-decision for work item {WorkItemId} abandoned: an accreditation number could not "
                    + "be resolved (correlation {CorrelationId}): {ErrorMessage}. No state was changed.",
                workItemId,
                correlationId,
                accreditationNumber.ErrorMessage
            );
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.AccreditationNumberUnavailable,
                "The re-accreditation decision could not be recorded because an accreditation number "
                    + "could not be obtained. No change was made; try again shortly."
            );
        }

        if (
            needsSubmitForDecision
            && await ApplySubmitForDecisionAsync(workItemId, user, cancellationToken) is { } failed
        )
        {
            return failed;
        }

        var result = await approvalService.RecordApprovedDeterminationAsync(
            workItemId,
            user,
            accreditationNumber,
            cancellationToken
        );

        if (result.IsSuccess)
        {
            logger.LogInformation(
                "Re-accreditation work item {WorkItemId} determined as approved by {UserId}; "
                    + "pending applicant contact.",
                workItemId,
                user.FindFirstValue("user:id")
            );
        }
        else
        {
            // If the hop ran, the item sits in
            // 'awaiting-decision', which this service accepts as an entry
            // state, so the identical call can simply be retried. The number
            // endpoint is idempotent, so a retry gets the same number back.
            logger.LogWarning(
                "Log-decision for work item {WorkItemId} failed at the step 1 approval ({FailureCode}); "
                    + "the call may be safely retried.",
                workItemId,
                result.FailureCode
            );
        }

        return result;
    }

    /// <summary>
    /// Applies the internal <c>submit-for-decision</c> hop. Returns the
    /// failure to hand back, or null when it landed. Nothing has been written
    /// on failure, so the caller can simply retry.
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


    /// <summary>
    /// RA-527 step 1 of a refusal. Takes the <c>submit-for-decision</c> hop if
    /// needed, then has the refusal service move the item to
    /// <c>rejected-pending-contact</c>. No number is minted.
    ///
    /// Deliberately no operator-journey push, as for an approval: the
    /// operator must not learn of the outcome before the regulator has
    /// contacted them. The SLA clock is left running — step 2 stops it.
    /// </summary>
    private async Task<WorkItemActionResult> RecordRefusalAsync(
        Guid workItemId,
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

        var result = await refusalService.RecordRefusedDeterminationAsync(
            workItemId,
            user,
            cancellationToken
        );

        if (result.IsSuccess)
        {
            logger.LogInformation(
                "Re-accreditation work item {WorkItemId} determined as refused by {UserId}; "
                    + "pending applicant contact.",
                workItemId,
                user.FindFirstValue("user:id")
            );
        }
        else
        {
            // If the hop ran, the item sits in 'awaiting-decision', which this
            // service accepts as an entry state, so the identical call can
            // simply be retried.
            logger.LogWarning(
                "Log-decision for work item {WorkItemId} failed at the step 1 refusal ({FailureCode}); "
                    + "the call may be safely retried.",
                workItemId,
                result.FailureCode
            );
        }

        return result;
    }

    /// <summary>
    /// RA-527: does the work item's own frozen snapshot (or the live type, for
    /// a legacy item with none) declare the step 1 transition
    /// <paramref name="actionId"/>? The approval and refusal services apply
    /// the same rule; this is the up-front copy so the item is refused before
    /// any side effect.
    /// </summary>
    private static bool Declares(WorkItem workItem, string actionId)
    {
        IWorkItemTemplate template = (IWorkItemTemplate?)workItem.TemplateSnapshot ?? s_type;
        return template.Transitions.Any(t =>
            string.Equals(t.ActionId, actionId, StringComparison.OrdinalIgnoreCase)
        );
    }
}
