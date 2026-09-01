using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Utils.Background;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.AspNetCore.HeaderPropagation;
using Microsoft.Extensions.Primitives;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-368: post-action hook that pushes every re-accreditation state
/// transition to the operator backend, so its own record of application
/// progress reflects the Case Management service's lifecycle beyond the query/resume round-trip
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
/// is out of scope for RA-368 — the Case Management service's own caseworker-facing withdraw UI is
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
///
/// RA-519: this hook is invoked synchronously, inside the very request that
/// triggered the transition — which, for the Case Management service's own
/// resume-from-query flow, is a request the Case Management service's own
/// webhook endpoint initiated into this service in the first place. Awaiting
/// the push (a callback back into that same Case Management service
/// endpoint, <c>.../case-management/{workItemId}/status</c>) inline here
/// would re-enter the Case Management service's write path while its own
/// originating request is still in flight, racing the Case Management
/// service's write to the same document and risking a lost update. So the
/// push (and the audit entry it produces) is deferred onto
/// <see cref="IBackgroundTaskQueue"/> — the same fire-and-forget mechanism
/// <see cref="ReAccreditationApprovalService.EnqueuePublishingAuditAsync"/>
/// uses — so this hook (and the request it runs inside) always returns
/// before the callback fires, never blocking on or racing the Case
/// Management service's own write.
///
/// RA-519 follow-up: <see cref="HeaderPropagationValues.Headers"/> is backed
/// by a static <c>AsyncLocal</c> that <c>app.UseHeaderPropagation()</c>
/// populates only for the lifetime of an inbound HTTP request — the DI
/// registration for <see cref="HeaderPropagationValues"/> itself is a
/// singleton, so the same instance is injected everywhere, but its
/// <c>Headers</c> getter/setter reads/writes that AsyncLocal, meaning it's
/// still request-scoped in practice. The queued job below runs on
/// <see cref="QueuedHostedService"/>'s own loop, entirely outside that
/// request's async flow, so the adapter's <c>"DefaultClient"</c> — wired with
/// <c>AddHeaderPropagation()</c> — would otherwise throw
/// (<c>HeaderPropagationValues.Headers has not been initialized</c>) the
/// moment <see cref="HeaderPropagationMessageHandler"/> tries to build the
/// outbound request. The allow-listed headers (tracing/correlation ids only —
/// see <c>ConfigureHeaderPropagation</c> in Program.cs) are read here, while
/// still inside the request, and written back into the queued job's own
/// AsyncLocal context before the adapter call.
/// </summary>
internal sealed class ReAccreditationStatusPushHook(
    IBackgroundTaskQueue backgroundTaskQueue,
    HeaderPropagationValues headerPropagationValues,
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

        // RA-519: capture everything the deferred job needs as plain values
        // up front — the work item and ClaimsPrincipal are in-memory state,
        // safe to close over, but the job itself must resolve its own scoped
        // services (adapter, audit appender) fresh, since it runs after this
        // request — and its DI scope — has ended.
        var workItemId = workItem.Id;
        var toStateId = workItem.StateId;
        var toStateDisplayName = ResolveStateDisplayName(workItem, toStateId);
        var actionDisplayName = ResolveActionDisplayName(workItem, actionId);
        var occurredAt = workItem.LastModifiedAt;

        // RA-519 follow-up: snapshot the allow-listed propagated headers
        // (tracing/correlation ids only) while still inside the request's
        // AsyncLocal context, so the queued job can restore them for itself —
        // see the class doc comment above.
        var propagatedHeaders = headerPropagationValues.Headers;

        var pushContext = new StatusPushContext(
            workItemId, correlationId, fromStateId, toStateId, toStateDisplayName,
            actionId, actionDisplayName, occurredAt, propagatedHeaders, user);

        try
        {
            await backgroundTaskQueue.QueueAsync(
                (scopedServices, ct) => RunQueuedPushAsync(scopedServices, pushContext, ct),
                cancellationToken);
        }
        catch (Exception ex)
        {
            // Hooks must never throw — a failure to even enqueue the push
            // must not unwind the already-persisted transition.
            logger.LogError(
                ex, "Unexpected failure queueing status-changed push for work item {WorkItemId} (correlation {CorrelationId}).",
                workItemId, correlationId);
        }
    }

    /// <summary>
    /// The deferred job itself — runs long after <see cref="OnActionAppliedAsync"/>
    /// (and the request it was called from) has returned, on
    /// <see cref="QueuedHostedService"/>'s own loop. Must uphold the same
    /// never-throws contract independently: a bad push must not take the
    /// background worker down or leave a "queued" job silently unaccounted
    /// for.
    /// </summary>
    private async Task RunQueuedPushAsync(
        IServiceProvider scopedServices,
        StatusPushContext context,
        CancellationToken ct)
    {
        try
        {
            // Restore the captured headers into this job's own AsyncLocal
            // context before the adapter builds its outbound request —
            // otherwise HeaderPropagationMessageHandler throws, since
            // nothing populates this AsyncLocal outside an HTTP request.
            // HeaderPropagationValues is a DI singleton, so resolving it
            // here (rather than closing over the constructor-injected
            // instance) isn't required for correctness, but keeps this
            // job's DI usage consistent with the adapter/appender below.
            scopedServices.GetRequiredService<HeaderPropagationValues>().Headers =
                context.PropagatedHeaders ?? new Dictionary<string, StringValues>();

            var adapter = scopedServices.GetRequiredService<IOperatorBackendPushAdapter>();
            var appender = scopedServices.GetRequiredService<IWorkItemAuditAppender>();

            var result = await adapter.PushStatusChangedAsync(
                context.WorkItemId, context.CorrelationId, context.FromStateId, context.ToStateId,
                context.ToStateDisplayName, context.ActionId, context.ActionDisplayName, context.OccurredAt, ct);

            // actionDisplayName/toStateDisplayName are included here (not
            // just on the wire push) because management-fe's audit-log
            // projection (statusPushDetailRows / summariseAuditEntry) reads
            // details.actionDisplayName / details.toStateDisplayName,
            // falling back to the raw ids only when absent — the canonical
            // action-entry key set documented in docs/work-items.md.
            var details = new Dictionary<string, string?>
            {
                ["actionId"] = context.ActionId,
                ["actionDisplayName"] = context.ActionDisplayName,
                ["fromStateId"] = context.FromStateId,
                ["toStateId"] = context.ToStateId,
                ["toStateDisplayName"] = context.ToStateDisplayName,
                ["correlationId"] = context.CorrelationId.ToString(),
            };

            await RecordPushOutcomeAsync(
                result, appender, context.WorkItemId, context.CorrelationId, details, context.User, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Unexpected failure pushing status-changed for work item {WorkItemId} (correlation {CorrelationId}).",
                context.WorkItemId, context.CorrelationId);
        }
    }

    /// <summary>
    /// RA-519 follow-up: the deferred job's own inputs, bundled so the
    /// closure handed to <see cref="IBackgroundTaskQueue.QueueAsync"/> and
    /// <see cref="RunQueuedPushAsync"/> take a single value instead of the
    /// ten separate plain-value parameters <see cref="OnActionAppliedAsync"/>
    /// captures (Sonar's parameter-count gate).
    /// </summary>
    private sealed record StatusPushContext(
        Guid WorkItemId,
        Guid CorrelationId,
        string FromStateId,
        string ToStateId,
        string ToStateDisplayName,
        string ActionId,
        string ActionDisplayName,
        DateTime OccurredAt,
        IDictionary<string, StringValues>? PropagatedHeaders,
        ClaimsPrincipal User);

    /// <summary>
    /// Records the <c>status-push-sent</c> / <c>status-push-skipped</c> /
    /// <c>status-push-failed</c> audit entry for the outcome of the deferred
    /// push, mirroring <see cref="ReAccreditationQueryPushHook"/>'s own
    /// pattern — see the class doc comment for why the skipped/failed
    /// outcomes are kept distinct.
    /// </summary>
    private async Task RecordPushOutcomeAsync(
        OperatorBackendPushResult result,
        IWorkItemAuditAppender appender,
        Guid workItemId,
        Guid correlationId,
        Dictionary<string, string?> details,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (result.IsSuccess)
        {
            var appended = await appender.AppendAsync(
                workItemId, "status-push-sent", "Status sent to the Registration & Accreditation service", details, user, ct);
            if (!appended)
            {
                logger.LogWarning(
                    "status-push-sent audit entry could not be persisted for work item {WorkItemId} (correlation {CorrelationId}).",
                    workItemId, correlationId);
            }
        }
        else if (result.IsSkipped)
        {
            // Deliberately disabled (OperatorBackendApi:Enabled=false) — not
            // an error, so logged at Debug and audited under a distinct
            // outcome that must never alert (MBE-F5).
            details["reason"] = result.ErrorMessage;
            logger.LogDebug(
                "Status push skipped for work item {WorkItemId} (correlation {CorrelationId}): {Reason}",
                workItemId, correlationId, result.ErrorMessage);
            var appended = await appender.AppendAsync(
                workItemId, "status-push-skipped", "Status not sent to the Registration & Accreditation service (disabled)", details, user, ct);
            if (!appended)
            {
                logger.LogWarning(
                    "status-push-skipped audit entry could not be persisted for work item {WorkItemId} (correlation {CorrelationId}).",
                    workItemId, correlationId);
            }
        }
        else
        {
            details["errorMessage"] = result.ErrorMessage;
            logger.LogError(
                "Push of status-changed for work item {WorkItemId} (correlation {CorrelationId}) failed: {ErrorMessage}",
                workItemId, correlationId, result.ErrorMessage);
            var appended = await appender.AppendAsync(
                workItemId, "status-push-failed", "Status failed to send to the Registration & Accreditation service", details, user, ct);
            if (!appended)
            {
                logger.LogWarning(
                    "status-push-failed audit entry could not be persisted for work item {WorkItemId} (correlation {CorrelationId}).",
                    workItemId, correlationId);
            }
        }
    }

    public Task OnAssignmentChangedAsync(
        WorkItem workItem, WorkItemAssignmentChange change, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Every action id whose declared transition <em>moves</em> a work item
    /// onto <c>queried</c> or <c>withdrawn</c> — i.e. exactly the
    /// query-during-* and withdraw/withdraw-during-* families, without
    /// restating their literal ids here. Computed once from a fresh
    /// <see cref="ReAccreditationType"/> instance, the same source of truth
    /// the engine itself validates transitions against.
    ///
    /// RA-351: self-loops are deliberately not excluded. The new
    /// <c>sla-extend</c> transition on <c>queried</c> (queried → queried)
    /// lands on <c>queried</c> but does not <em>move</em> the item there — it
    /// is an in-place SLA extension, the same kind of status change the
    /// assessment-in-progress <c>sla-extend</c> self-loop pushes. Without the
    /// self-loop guard, sharing the <c>sla-extend</c> action id across both
    /// self-loops would wrongly exclude the whole action from status pushes.
    /// </summary>
    private static HashSet<string> BuildExcludedActionIds()
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var transition in s_type.Transitions)
        {
            if (string.Equals(
                    transition.FromStateId,
                    transition.ToStateId,
                    StringComparison.OrdinalIgnoreCase))
            {
                // Self-loop: an in-place action, not a move onto queried/withdrawn.
                continue;
            }

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
