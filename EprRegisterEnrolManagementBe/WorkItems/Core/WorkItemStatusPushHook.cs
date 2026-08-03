using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-368: framework-level post-action hook that pushes every work item
/// state transition to the operator backend, so its own record of
/// application progress reflects CM's lifecycle beyond the query/resume
/// round-trip already covered by <c>ReAccreditationQueryPushHook</c>.
///
/// Fires from every generic transition (<see cref="WorkItemService.ApplyActionAsync"/>)
/// and from any bespoke module service that invokes
/// <see cref="IWorkItemPostActionHook.OnActionAppliedAsync"/> directly (e.g.
/// the re-accreditation approval service), since it is registered like any
/// other <see cref="IWorkItemPostActionHook"/>. One bypass needs explicit
/// wiring: a hook that mutates state directly without going through
/// <see cref="WorkItemService.ApplyActionAsync"/> (e.g.
/// <c>ReAccreditationDulyMadeHook</c>) must call this hook itself.
///
/// Skips the four <c>query-during-*</c> action ids (query keeps its own
/// richer <c>/query</c> push) and every <c>withdraw</c>/<c>withdraw-during-*</c>
/// action id (withdrawal is out of scope for RA-368 — CM's own
/// caseworker-facing withdraw UI is being hidden by a separate, future
/// ticket).
///
/// Never throws — a push failure must not unwind the already-persisted
/// transition (the <see cref="IWorkItemPostActionHook"/> contract). Records
/// the outcome as a <c>status-push-sent</c> / <c>status-push-skipped</c> /
/// <c>status-push-failed</c> audit entry, mirroring
/// <c>ReAccreditationQueryPushHook</c>'s own pattern. <c>status-push-skipped</c>
/// (the push is deliberately disabled, <c>OperatorBackendApi:Enabled=false</c>)
/// is kept distinct from <c>status-push-failed</c> (an attempted push that
/// errored) — same MBE-F5 rationale. A failed audit append is logged, not
/// retried.
/// </summary>
internal sealed class WorkItemStatusPushHook(
    IOperatorBackendPushAdapter pushAdapter,
    IWorkItemRegistry registry,
    IWorkItemAuditAppender auditAppender,
    ILogger<WorkItemStatusPushHook> logger) : IWorkItemPostActionHook
{
    private static readonly HashSet<string> s_excludedActionIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "query-during-duly-making",
        "query-during-duly-made",
        "query-during-assessment",
        "query-during-decision",
        "withdraw",
        "withdraw-during-duly-made",
        "withdraw-during-assessment",
        "withdraw-during-decision",
        "withdraw-during-query",
        "withdraw-during-updated",
    };

    public Task OnSubmittedAsync(WorkItem workItem, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task OnActionAppliedAsync(
        WorkItem workItem,
        string actionId,
        string fromStateId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (s_excludedActionIds.Contains(actionId))
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

            var details = new Dictionary<string, string?>
            {
                ["actionId"] = actionId,
                ["fromStateId"] = fromStateId,
                ["toStateId"] = toStateId,
                ["correlationId"] = correlationId.ToString(),
            };

            if (result.IsSuccess)
            {
                var appended = await auditAppender.AppendAsync(
                    workItem.Id, "status-push-sent", "Status pushed to operator backend", details, user, cancellationToken);
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
                    workItem.Id, "status-push-skipped", "Status push to operator backend skipped", details, user, cancellationToken);
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
                    workItem.Id, "status-push-failed", "Status push to operator backend failed", details, user, cancellationToken);
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
    /// Resolves the human-readable label for <paramref name="stateId"/> from
    /// the work item's frozen template snapshot (preferred, so historical
    /// items keep rendering as they did when assessed) or the live
    /// registered type as a fallback. Falls back to the raw state id itself
    /// if neither knows it, rather than throwing — this hook must never
    /// unwind the transition it is reporting on.
    /// </summary>
    private string ResolveStateDisplayName(WorkItem workItem, string stateId)
    {
        IWorkItemTemplate? template = workItem.TemplateSnapshot;
        template ??= registry.Find(workItem.TypeId);
        var displayName = template?.States.FirstOrDefault(
            state => string.Equals(state.Id, stateId, StringComparison.OrdinalIgnoreCase))?.DisplayName;
        return displayName ?? stateId;
    }

    /// <summary>
    /// Resolves the human-readable label for <paramref name="actionId"/> from
    /// the <c>action-applied</c> audit entry the caller (the generic engine,
    /// a bespoke approval service, or a task-completion hook) always appends
    /// immediately before invoking post-action hooks. Preferred over a
    /// template transition lookup because some actions this hook fires for
    /// (e.g. <c>approve</c>, <c>duly-make</c>) are handled entirely outside
    /// the declared transition set and have no <see cref="WorkItemTransition"/>
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
