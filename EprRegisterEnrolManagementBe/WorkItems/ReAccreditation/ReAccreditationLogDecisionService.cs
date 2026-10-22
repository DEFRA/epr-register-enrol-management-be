using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
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
/// the caller names an outcome, never an action id. Both
/// <c>submit-for-decision</c> and <c>reject</c> are declared
/// <see cref="WorkItemTransition.CallerInvocable"/> <c>false</c>, so this
/// service — calling <see cref="IWorkItemService.ApplyActionAsync"/> directly
/// with a server-computed action id — is the only route to either.
///
/// RA-527: approving is step 1 of a two-step determination. It mints the
/// accreditation number and delegates to
/// <see cref="IReAccreditationApprovalService.RecordApprovedDeterminationAsync"/>,
/// which stamps it and moves the item to the non-terminal
/// <c>approved-pending-contact</c> state. The outcome is not applied: no
/// Registration &amp; Accreditation service push, no SLA clock stop, no
/// publishing job and no Decision email — those belong to step 2
/// ("confirm applicant contact"), so the operator never learns the outcome
/// before the regulator has contacted them.
///
/// epr-p86e / RA-410: a refusal (still single-step until RA-527 task 005) is
/// gated on the Registration &amp;
/// Accreditation service status push. The Registration &amp; Accreditation
/// service push is fired ONCE here, as a pre-commit gate for the
/// final outcome, BEFORE either internal hop is persisted — and the
/// post-action <see cref="ReAccreditationStatusPushHook"/> is suppressed for
/// the three decision actions so the push happens exactly once. If the
/// Registration &amp; Accreditation service cannot
/// be reached within its retry budget the decision is abandoned with a 500 and
/// nothing is written, so the item stays exactly where it was rather than
/// stranding in <c>awaiting-decision</c> (the bug this change fixes: the old
/// post-commit hook pushed on BOTH hops, and when the Registration &amp;
/// Accreditation service hung the request's time
/// budget was exhausted, cancelling the token mid-approval). A disabled push
/// (<see cref="OperatorBackendPushResult.IsSkipped"/>) is a pass — decisions
/// must still work where the push is switched off.
///
/// The Registration &amp; Accreditation service push body is state-transition only (no accreditation id or anything
/// minted during approval), which is exactly why it can be sent before the
/// outcome is committed.
/// </summary>
internal sealed class ReAccreditationLogDecisionService(
    IWorkItemPersistence persistence,
    IWorkItemService engine,
    IReAccreditationApprovalService approvalService,
    IOperatorBackendPushAdapter pushAdapter,
    IWorkItemAuditAppender auditAppender,
    ILogger<ReAccreditationLogDecisionService> logger,
    TimeProvider? timeProvider = null
) : IReAccreditationLogDecisionService
{
    private const string AssessmentStateId = "assessment-in-progress";
    private const string AwaitingDecisionStateId = "awaiting-decision";
    private const string SubmitForDecisionActionId = "submit-for-decision";
    private const string RejectActionId = "reject";
    private const string ApprovedStateId = ReAccreditationType.ApprovedStateId;
    private const string RejectedStateId = "rejected";
    private const string WithdrawnStateId = "withdrawn";

    // epr-p86e / RA-410: the fromState reported to the Registration &
    // Accreditation service on the decision push.
    //
    // DELIBERATE CONTRACT-MATCH — CONFIRM WITH THE REGISTRATION &
    // ACCREDITATION SERVICE BACKEND OWNER.
    // The old post-commit push for approve/reject reported fromState
    // 'awaiting-decision' (the item HAD been persisted there by the first hop
    // before the push fired). Here the push fires BEFORE any hop is persisted,
    // so at push time the item is still in 'assessment-in-progress'. We keep
    // reporting 'awaiting-decision' to match the exact value the Registration
    // & Accreditation service has always
    // received, so this change is invisible to the Registration &
    // Accreditation service contract. The Registration & Accreditation
    // service is not in
    // the test stack and cannot be verified here — whoever owns the
    // Registration & Accreditation service backend
    // should confirm it does not care about (or actively expects) this value.
    private const string PushFromStateId = AwaitingDecisionStateId;

    private static readonly ReAccreditationType s_type = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ReAccreditationDecisionPush _decisionPush = new(pushAdapter, auditAppender, logger);

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
        if (RequireActorIdentity(user) is { } identityFailure)
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

        // RA-527: an approval is step 1 of two — it lands in the non-terminal
        // approved-pending-contact state, not 'approved'. A refusal is still
        // applied in one step (task 005 changes it).
        var targetStateId = isApproval
            ? ReAccreditationType.ApprovedPendingContactStateId
            : RejectedStateId;

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
        // carry terminal metadata. RA-527: this includes an Approved replay
        // on an application already Granted — the determination was made
        // once, and step 2 is what applied it.
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
        // else — including RA-527's approved-pending-contact, whose
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
        if (isApproval && !DeclaresStepOneApproval(workItem))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item {workItemId} is on template '{workItem.TemplateVersion}', which does not "
                    + $"declare '{ReAccreditationType.MakeDeterminationApprovedActionId}'."
            );
        }

        return isApproval
            ? await RecordApprovalAsync(workItemId, needsSubmitForDecision, user, cancellationToken)
            : await RecordRefusalAsync(workItem, needsSubmitForDecision, user, cancellationToken);
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
            // As for a refusal below: if the hop ran, the item sits in
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
    /// A refusal, still applied in one step (RA-527 task 005 splits it):
    /// pre-commit operator-journey push, then the hop, then <c>reject</c>.
    /// </summary>
    private async Task<WorkItemActionResult> RecordRefusalAsync(
        WorkItem workItem,
        bool needsSubmitForDecision,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var workItemId = workItem.Id;

        // epr-p86e / RA-410 PRE-COMMIT REGISTRATION & ACCREDITATION SERVICE GATE. Notify the operator journey of
        // the final outcome BEFORE persisting either internal hop. On failure,
        // nothing has been written, so there is nothing to revert — the item
        // stays exactly where it was and the caller gets a generic 500.
        var delivery = await _decisionPush.PushAsync(
            workItem,
            PushFromStateId,
            RejectedStateId,
            RejectActionId,
            "Reject",
            _timeProvider.GetUtcNow().UtcDateTime,
            user,
            cancellationToken
        );

        if (delivery.Failed)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UpstreamNotificationFailed,
                "The re-accreditation decision could not be recorded because the operator journey could not "
                    + "be notified. No change was made; try again shortly."
            );
        }

        if (
            needsSubmitForDecision
            && await ApplySubmitForDecisionAsync(workItemId, user, cancellationToken) is { } failed
        )
        {
            return failed;
        }

        var result = await engine.ApplyActionAsync(
            workItemId,
            RejectActionId,
            user,
            cancellationToken
        );

        if (result.IsSuccess)
        {
            logger.LogInformation(
                "Re-accreditation work item {WorkItemId} decided as {Outcome} by {UserId}",
                workItemId,
                RejectedStateId,
                user.FindFirstValue("user:id")
            );

            // Record the single decision push's outcome now the decision has
            // landed, so it sits after the action-applied entries, exactly as
            // the post-commit hook's entry did.
            await _decisionPush.RecordDeliveredAsync(delivery, user, cancellationToken);
        }
        else
        {
            // The submit-for-decision hop, if it ran, has already been
            // persisted. That is survivable rather than corrupt: the item now
            // sits in 'awaiting-decision', which this method accepts as an
            // entry state, so replaying the caller's identical request
            // completes the decision. The Registration & Accreditation service was already told the final outcome by
            // the gate above, so the replay does not re-notify it needlessly
            // beyond a single idempotent status upsert.
            logger.LogWarning(
                "Log-decision for work item {WorkItemId} failed at the {Outcome} step ({FailureCode}); "
                    + "the work item is in '{StateId}' and the call may be safely retried.",
                workItemId,
                RejectedStateId,
                result.FailureCode,
                result.WorkItem?.StateId ?? AwaitingDecisionStateId
            );
        }

        return result;
    }

    /// <summary>
    /// RA-527: does the work item's own frozen snapshot (or the live type, for
    /// a legacy item with none) declare the step 1 approval transition? The
    /// approval service applies the same rule; this is the up-front copy so
    /// the refusal happens before any side effect.
    /// </summary>
    private static bool DeclaresStepOneApproval(WorkItem workItem)
    {
        IWorkItemTemplate template = (IWorkItemTemplate?)workItem.TemplateSnapshot ?? s_type;
        return template.Transitions.Any(t =>
            string.Equals(
                t.ActionId,
                ReAccreditationType.MakeDeterminationApprovedActionId,
                StringComparison.OrdinalIgnoreCase
            )
        );
    }

    private static WorkItemActionResult? RequireActorIdentity(ClaimsPrincipal user) =>
        string.IsNullOrWhiteSpace(user.FindFirstValue("user:id"))
            ? WorkItemActionResult.Failure(
                WorkItemActionFailureCode.MissingActorIdentity,
                "Mutating this work item requires an authenticated end user; "
                    + "the request did not include a 'user:id' claim."
            )
            : null;
}
