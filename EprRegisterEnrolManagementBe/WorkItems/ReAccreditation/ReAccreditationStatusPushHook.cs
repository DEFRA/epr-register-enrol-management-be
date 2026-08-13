using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-368: post-action hook that pushes every re-accreditation state
/// transition to the operator backend, so its own record of application
/// progress reflects CM's lifecycle beyond the query/resume round-trip
/// already covered by <see cref="ReAccreditationQueryPushHook"/>.
///
/// Fires from every generic transition (<see cref="WorkItemService.ApplyActionAsync"/>)
/// and from any bespoke module service that invokes
/// <see cref="IWorkItemPostActionHook.OnActionAppliedAsync"/> directly (e.g.
/// <see cref="ReAccreditationApprovalService"/>), since it is registered like
/// any other <see cref="IWorkItemPostActionHook"/>. One bypass needs explicit
/// wiring: <see cref="ReAccreditationDulyMadeHook"/> mutates state directly
/// without going through <see cref="WorkItemService.ApplyActionAsync"/>, so
/// it calls this hook itself.
///
/// Skips every action whose declared <see cref="WorkItemTransition.ToStateId"/>
/// is <c>queried</c> (query keeps its own richer <c>/query</c> push, see
/// <see cref="ReAccreditationQueryPushHook"/>) or <c>withdrawn</c> (withdrawal
/// is out of scope for RA-368 — CM's own caseworker-facing withdraw UI is
/// being hidden by a separate, future ticket). Those are derived from
/// <see cref="ReAccreditationType.Transitions"/> itself, rather than
/// restating the individual action ids, so a future query/withdraw
/// transition is excluded automatically instead of needing this hook
/// updated in lockstep.
///
/// epr-p86e / RA-410: the three decision actions (<c>submit-for-decision</c>,
/// <c>approve</c>, <c>reject</c>) are also excluded — see
/// <see cref="BuildExcludedActionIds"/>. Their operator-journey push is owned
/// by <see cref="ReAccreditationLogDecisionService"/>, which fires it exactly
/// once as a pre-commit gate for the final outcome; this hook pushing again
/// after each committed hop was the double-push that stranded applications in
/// <c>awaiting-decision</c> when the operator journey was down.
///
/// Never throws — a push failure must not unwind the already-persisted
/// transition (the <see cref="IWorkItemPostActionHook"/> contract). Records
/// the outcome as a <c>status-push-sent</c> / <c>status-push-skipped</c> /
/// <c>status-push-failed</c> audit entry, mirroring
/// <see cref="ReAccreditationQueryPushHook"/>'s own pattern (and matching the
/// <c>ACTION_DISPLAY_NAMES</c> wired up for these three actions in
/// management-fe). <c>status-push-skipped</c> (the push is deliberately
/// disabled, <c>OperatorBackendApi:Enabled=false</c>) is kept distinct from
/// <c>status-push-failed</c> (an attempted push that errored) — same MBE-F5
/// rationale. A failed audit append is logged, not retried.
/// </summary>
internal sealed class ReAccreditationStatusPushHook(
    IOperatorBackendPushAdapter pushAdapter,
    IWorkItemAuditAppender auditAppender,
    ILogger<ReAccreditationStatusPushHook> logger) : IWorkItemPostActionHook
{
    private static readonly ReAccreditationType s_type = new();

    private static readonly HashSet<string> s_excludedActionIds = BuildExcludedActionIds();

    public Task OnSubmittedAsync(WorkItem workItem, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task OnActionAppliedAsync(
        WorkItem workItem,
        string actionId,
        string fromStateId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(workItem.TypeId, ReAccreditationType.Id, StringComparison.OrdinalIgnoreCase)
            || s_excludedActionIds.Contains(actionId))
        {
            return;
        }

        // RA-368 cross-repo contract (mirrors RA-311/MBE-1): one correlation
        // id per push, generated here and threaded through the HTTP header,
        // every log line, and every audit entry for this push.
        var correlationId = Guid.NewGuid();

        try
        {
            var toStateId = workItem.StateId;
            var toStateDisplayName = ResolveStateDisplayName(workItem, toStateId);
            var actionDisplayName = ResolveActionDisplayName(workItem, actionId);
            var occurredAt = workItem.LastModifiedAt;

            var result = await pushAdapter.PushStatusChangedAsync(
                workItem.Id, correlationId, fromStateId, toStateId, toStateDisplayName,
                actionId, actionDisplayName, occurredAt, cancellationToken);

            // actionDisplayName/toStateDisplayName are included here (not
            // just on the wire push) because management-fe's audit-log
            // projection (statusPushDetailRows / summariseAuditEntry) reads
            // details.actionDisplayName / details.toStateDisplayName,
            // falling back to the raw ids only when absent — the canonical
            // action-entry key set documented in docs/work-items.md.
            var details = new Dictionary<string, string?>
            {
                ["actionId"] = actionId,
                ["actionDisplayName"] = actionDisplayName,
                ["fromStateId"] = fromStateId,
                ["toStateId"] = toStateId,
                ["toStateDisplayName"] = toStateDisplayName,
                ["correlationId"] = correlationId.ToString(),
            };

            if (result.IsSuccess)
            {
                var appended = await auditAppender.AppendAsync(
                    workItem.Id, "status-push-sent", "Status sent to OJ", details, user, cancellationToken);
                if (!appended)
                {
                    logger.LogWarning(
                        "status-push-sent audit entry could not be persisted for work item {WorkItemId} (correlation {CorrelationId}).",
                        workItem.Id, correlationId);
                }
            }
            else if (result.IsSkipped)
            {
                // Deliberately disabled (OperatorBackendApi:Enabled=false) —
                // not an error, so logged at Debug and audited under a
                // distinct outcome that must never alert (MBE-F5).
                details["reason"] = result.ErrorMessage;
                logger.LogDebug(
                    "Status push skipped for work item {WorkItemId} (correlation {CorrelationId}): {Reason}",
                    workItem.Id, correlationId, result.ErrorMessage);
                var appended = await auditAppender.AppendAsync(
                    workItem.Id, "status-push-skipped", "Status not sent to OJ (disabled)", details, user, cancellationToken);
                if (!appended)
                {
                    logger.LogWarning(
                        "status-push-skipped audit entry could not be persisted for work item {WorkItemId} (correlation {CorrelationId}).",
                        workItem.Id, correlationId);
                }
            }
            else
            {
                details["errorMessage"] = result.ErrorMessage;
                logger.LogError(
                    "Push of status-changed for work item {WorkItemId} (correlation {CorrelationId}) failed: {ErrorMessage}",
                    workItem.Id, correlationId, result.ErrorMessage);
                var appended = await auditAppender.AppendAsync(
                    workItem.Id, "status-push-failed", "Status failed to send to OJ", details, user, cancellationToken);
                if (!appended)
                {
                    logger.LogWarning(
                        "status-push-failed audit entry could not be persisted for work item {WorkItemId} (correlation {CorrelationId}).",
                        workItem.Id, correlationId);
                }
            }
        }
        catch (Exception ex)
        {
            // Hooks must never throw — a push failure must not unwind the
            // already-persisted transition.
            logger.LogError(
                ex, "Unexpected failure pushing status-changed for work item {WorkItemId} (correlation {CorrelationId}).",
                workItem.Id, correlationId);
        }
    }

    public Task OnAssignmentChangedAsync(
        WorkItem workItem, WorkItemAssignmentChange change, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Every action id whose declared transition lands on <c>queried</c> or
    /// <c>withdrawn</c> — i.e. exactly the query-during-* and
    /// withdraw/withdraw-during-* families, without restating their literal
    /// ids here. Computed once from a fresh <see cref="ReAccreditationType"/>
    /// instance, the same source of truth the engine itself validates
    /// transitions against.
    /// </summary>
    private static HashSet<string> BuildExcludedActionIds()
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var transition in s_type.Transitions)
        {
            if (string.Equals(transition.ToStateId, "queried", StringComparison.OrdinalIgnoreCase)
                || string.Equals(transition.ToStateId, "withdrawn", StringComparison.OrdinalIgnoreCase))
            {
                excluded.Add(transition.ActionId);
            }
        }

        // epr-p86e / RA-410: the three decision actions are excluded here so
        // this post-action hook never pushes for them. The operator-journey
        // push for a decision is owned by ReAccreditationLogDecisionService,
        // which fires it exactly ONCE as a pre-commit gate for the final
        // outcome — before either internal hop is persisted. Left in, this
        // hook would push again after each committed transition, which is the
        // double-push (submit-for-decision + approve/reject) that stranded
        // applications in 'awaiting-decision' when the operator journey was
        // unreachable. 'approve' has no declared transition (it is handled by
        // ReAccreditationApprovalService), so it must be listed literally
        // rather than derived from the transition set.
        excluded.Add("submit-for-decision");
        excluded.Add("approve");
        excluded.Add("reject");
        return excluded;
    }

    /// <summary>
    /// Resolves the human-readable label for <paramref name="stateId"/> from
    /// the work item's frozen template snapshot (preferred, so historical
    /// items keep rendering as they did when assessed) or the live type as a
    /// fallback. Falls back to the raw state id itself if neither knows it,
    /// rather than throwing — this hook must never unwind the transition it
    /// is reporting on.
    /// </summary>
    private static string ResolveStateDisplayName(WorkItem workItem, string stateId)
    {
        IWorkItemTemplate template = (IWorkItemTemplate?)workItem.TemplateSnapshot ?? s_type;
        var displayName = template.States.FirstOrDefault(
            state => string.Equals(state.Id, stateId, StringComparison.OrdinalIgnoreCase))?.DisplayName;
        return displayName ?? stateId;
    }

    /// <summary>
    /// Resolves the human-readable label for <paramref name="actionId"/> from
    /// the <c>action-applied</c> audit entry the caller (the generic engine,
    /// <see cref="ReAccreditationApprovalService"/>, or
    /// <see cref="ReAccreditationDulyMadeHook"/>) always appends immediately
    /// before invoking post-action hooks. Preferred over a template
    /// transition lookup because some actions this hook fires for (e.g.
    /// <c>approve</c>, <c>duly-make</c>) are handled entirely outside the
    /// declared transition set and have no <see cref="WorkItemTransition"/>
    /// to look up. Falls back to the raw action id if no matching entry is
    /// found.
    /// </summary>
    private static string ResolveActionDisplayName(WorkItem workItem, string actionId)
    {
        for (var i = workItem.AuditLog.Count - 1; i >= 0; i--)
        {
            var entry = workItem.AuditLog[i];
            if (!string.Equals(entry.Action, "action-applied", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.Details.TryGetValue("actionId", out var entryActionId)
                && string.Equals(entryActionId, actionId, StringComparison.OrdinalIgnoreCase)
                && entry.Details.TryGetValue("actionDisplayName", out var displayName)
                && !string.IsNullOrWhiteSpace(displayName))
            {
                return displayName!;
            }
        }

        return actionId;
    }
}
