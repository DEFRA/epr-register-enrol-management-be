using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-523 default <see cref="IReAccreditationPaymentReceivedService"/>.
///
/// A regulator queries an application that has already been duly made, the
/// operator responds, and <see cref="ReAccreditationResumeService"/> leaves the
/// item in the non-terminal <c>updated</c> waypoint (RA-337). Before this
/// service the only way onwards was <c>continue-review-during-duly-made</c>,
/// which returned it to <c>duly-made</c> — so an application that had already
/// been duly made came back to the regulator as "Duly made" a second time.
/// This carries it forward to <c>assessment-in-progress</c> instead: exactly
/// where <c>payment-received</c> would have taken it had the query never been
/// raised.
///
/// Mirrors <see cref="ReAccreditationContinueReviewService"/>'s resolution
/// strategy — the caller never names an action — with one addition that is the
/// whole point of the class: a POSITIVE origin check.
///
/// The engine keys transitions on <c>FromStateId</c>, and
/// <c>payment-received-during-duly-made</c> shares <c>updated</c> with the four
/// <c>continue-review-during-*</c> transitions and
/// <c>withdraw-during-updated</c>. So "is this item in 'updated'?" is NOT
/// enough to decide this action applies. The originating state must resolve to
/// <c>duly-made</c> specifically. An item queried out of <c>submitted</c> has
/// never been duly made: carrying it to assessment would skip the step that
/// captures the payment date and anchors the 12-week SLA clock, leaving an
/// application under assessment with no clock running at all. Because the check
/// is positive rather than a blacklist, every origin other than
/// <c>duly-made</c> — including one that cannot be resolved — is refused rather
/// than silently acquiring new behaviour.
///
/// Like continue-review, this touches no fields itself: it delegates to
/// <see cref="IWorkItemService.ApplyActionAsync"/>, which writes state,
/// timestamp and the generic <c>action-applied</c> audit entry and nothing
/// else. Two consequences worth stating because they are the ticket's
/// acceptance criteria rather than incidental:
/// <list type="bullet">
///   <item>The assignee is PRESERVED. Nothing here writes
///   <see cref="WorkItem.AssignedToId"/> and friends; the only production code
///   that clears them is <see cref="IWorkItemService.UnassignAsync"/>.</item>
///   <item>The SLA clock is UNTOUCHED. It was started once, at duly making,
///   anchored to the entered payment date. Nothing here writes
///   <see cref="WorkItem.SlaClock"/>, so the hop cannot restart or re-anchor
///   it.</item>
/// </list>
/// </summary>
internal sealed class ReAccreditationPaymentReceivedService(
    IWorkItemPersistence persistence,
    IWorkItemRegistry registry,
    IWorkItemService engine,
    ILogger<ReAccreditationPaymentReceivedService> logger) : IReAccreditationPaymentReceivedService
{
    private const string ActionId = "payment-received-during-duly-made";

    /// <summary>
    /// The one originating state this action is valid for. Kept as a literal
    /// rather than derived, because it is a business rule ("only an already
    /// duly-made application may skip straight to assessment"), not a
    /// consequence of the state graph.
    /// </summary>
    private const string RequiredOriginStateId = "duly-made";

    private const string TargetStateId = "assessment-in-progress";

    public async Task<WorkItemActionResult> RecordPaymentReceivedAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);

        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'.");
        }

        if (!string.Equals(workItem.TypeId, ReAccreditationType.Id, StringComparison.OrdinalIgnoreCase))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                $"Work item {workItemId} is of type '{workItem.TypeId}', not '{ReAccreditationType.Id}'.");
        }

        var template = WorkItemEngineRules.ResolveTemplate(workItem, registry);
        if (template is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                $"Work item {workItemId} references unregistered type '{workItem.TypeId}' "
                    + "and has no stored template snapshot.");
        }

        if (!string.Equals(
                workItem.StateId,
                ReAccreditationUpdatedOrigin.StateId,
                StringComparison.OrdinalIgnoreCase))
        {
            // A genuinely concurrent/duplicate call (a double-click, a retried
            // request) must not fail the caller. Once the item has reached the
            // one state this action could have put it in, treat the repeat as a
            // no-op success. Anything else is a real conflict: this work item
            // was never waiting on this call.
            //
            // The replay set is deliberately the single target state, not the
            // four-state set continue-review uses. Continue-review has four
            // legitimate destinations; this action has exactly one, so widening
            // the set would report success for an item that went somewhere this
            // call could not have sent it.
            if (string.Equals(workItem.StateId, TargetStateId, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "Payment-received for work item {WorkItemId} is a no-op: already in state '{StateId}'.",
                    workItemId, workItem.StateId);
                return WorkItemActionResult.IdempotentReplay(workItem);
            }

            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item {workItemId} is in state '{workItem.StateId}', not "
                    + $"'{ReAccreditationUpdatedOrigin.StateId}', so payment received cannot be recorded.");
        }

        // THE ORIGIN GUARD. Shared with ReAccreditationOriginStateResolver, so
        // the origin the frontend renders its call to action from and the origin
        // this service acts on are the same derivation — a caseworker can never
        // be offered one state's CTA and be carried somewhere else.
        //
        // Null here means the item's own frozen snapshot predates the
        // continue-review-during-* transitions (pre-v8), so the origin is
        // genuinely unknowable. Refusing is correct: guessing could send an
        // application forward past duly making.
        var originStateId = ReAccreditationUpdatedOrigin.ResolveOriginatingStateId(workItem, template);

        if (!string.Equals(originStateId, RequiredOriginStateId, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Payment-received refused for work item {WorkItemId}: originating state resolved to "
                    + "'{OriginStateId}', not '{RequiredOriginStateId}'.",
                workItemId, originStateId ?? "<unresolved>", RequiredOriginStateId);

            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item {workItemId} was queried from "
                    + $"'{originStateId ?? "an unresolved state"}', not '{RequiredOriginStateId}', so it "
                    + "cannot skip straight to assessment. It must continue through its own review path.");
        }

        // The transition must be declared by the item's OWN template, not merely
        // by the live type. Template versioning is the framework's hard rule: an
        // in-flight v13 item is evaluated under v13 rules.
        // ReAccreditationPaymentReceivedDulyMadeSnapshotMigration adds the
        // transition to every pre-v14 snapshot at startup and retries on each
        // boot, so this refusal is a transient "migration has not caught up
        // yet", never a permanent dead end. Mirrors the same guard in
        // ReAccreditationDulyMakingService.
        if (!template.Transitions.Any(t =>
                string.Equals(t.ActionId, ActionId, StringComparison.OrdinalIgnoreCase)))
        {
            logger.LogWarning(
                "Work item {WorkItemId} cannot record payment received: its template snapshot "
                    + "({TemplateVersion}) does not declare the '{ActionId}' transition. "
                    + "ReAccreditationPaymentReceivedDulyMadeSnapshotMigration has not yet patched it.",
                workItem.Id, template.TemplateVersion, ActionId);

            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item '{workItemId}' was submitted under template version "
                    + $"'{template.TemplateVersion}', which does not support the '{ActionId}' action. "
                    + "Retry shortly; if this persists, the snapshot migration needs investigating.");
        }

        var result = await engine.ApplyActionAsync(workItemId, ActionId, user, cancellationToken);

        if (result.IsSuccess)
        {
            logger.LogInformation(
                "Re-accreditation work item {WorkItemId} moved from '{StateId}' to assessment by "
                    + "{UserId} via {ActionId} (queried from '{OriginStateId}')",
                workItemId, ReAccreditationUpdatedOrigin.StateId, user.FindFirstValue("user:id"),
                ActionId, originStateId);
        }

        return result;
    }
}
