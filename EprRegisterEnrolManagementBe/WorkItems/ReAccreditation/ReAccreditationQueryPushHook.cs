using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using MongoDB.Bson.Serialization;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-311/MBE-1: post-action hook that pushes the query note and queried
/// sections to the operator backend whenever a re-accreditation application
/// is queried, so the operator's own record reflects it without polling.
///
/// Fires on the same four actions as the "Queried" branch of
/// <see cref="ReAccreditationNotificationHook"/>
/// (<c>query-during-duly-making</c>/<c>-duly-made</c>/<c>-assessment</c>/
/// <c>-decision</c>). Reads <see cref="ReAccreditationPayload.CurrentQuery"/>
/// off the (already-persisted) <see cref="WorkItem.Payload"/> this hook is
/// handed — safe because <see cref="ReAccreditationQueryService"/> stamps
/// <c>currentQuery</c> via a direct write *before* calling
/// <see cref="IWorkItemService.ApplyActionAsync"/>, which re-loads the
/// document before persisting the transition, so the instance handed to
/// post-action hooks already carries it (the same ordering guarantee
/// <see cref="ReAccreditationNotificationHook"/>'s own <c>Queried</c>
/// template already relies on).
///
/// RA-000: the query-during-* transitions are not caller-invocable, so only
/// the query service applies them. As defence in depth the hook still proves
/// the stamp belongs to this transition (<see cref="ReAccreditationFreshQuery"/>)
/// and, when it cannot, or when the payload cannot be read, pushes nothing,
/// logs at Error and records <c>query-push-failed</c> with a <c>reason</c>.
///
/// Never throws — a push failure must not unwind the already-persisted
/// query transition (the <see cref="IWorkItemPostActionHook"/> contract).
/// Records the outcome as a <c>query-push-sent</c> / <c>query-push-skipped</c>
/// / <c>query-push-failed</c> audit entry, mirroring
/// <see cref="ReAccreditationNotificationHook"/>'s own
/// <c>notification-sent</c>/<c>notification-failed</c> pattern.
/// <c>query-push-skipped</c> (the push is deliberately disabled,
/// <c>OperatorBackendApi:Enabled=false</c>) is kept distinct from
/// <c>query-push-failed</c> (an attempted push that errored) — MBE-F5. A
/// failed audit append is logged, not retried.
/// </summary>
internal sealed class ReAccreditationQueryPushHook(
    IOperatorBackendPushAdapter pushAdapter,
    IWorkItemAuditAppender auditAppender,
    ILogger<ReAccreditationQueryPushHook> logger) : IWorkItemPostActionHook
{
    private static readonly HashSet<string> s_queryActionIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "query-during-duly-making",
        "query-during-duly-made",
        "query-during-assessment",
        "query-during-decision",
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
        if (!string.Equals(workItem.TypeId, ReAccreditationType.Id, StringComparison.OrdinalIgnoreCase)
            || !s_queryActionIds.Contains(actionId))
        {
            return;
        }

        // RA-311/MBE-1 cross-repo contract: one correlation id per push,
        // generated here (the point where the outbound call to the operator
        // backend originates) and threaded through the HTTP header, every
        // log line, and every audit entry for this push so the two
        // services' logs can be joined on a single value.
        var correlationId = Guid.NewGuid();

        try
        {
            ReAccreditationPayload payload;
            try
            {
                payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Query push for work item {WorkItemId} (correlation {CorrelationId}) not attempted: "
                    + "the payload could not be deserialised.",
                    workItem.Id, correlationId);
                await RecordNotAttemptedAsync(
                    workItem, actionId, correlationId, "payload could not be deserialised", user, cancellationToken);
                return;
            }

            // RA-000: never push a query this operation did not stamp. A
            // missing currentQuery used to push an empty note and no sections
            // (rejected by the operator backend), and a stale one re-sent the
            // previous query, which the operator backend accepted.
            if (!ReAccreditationFreshQuery.TryVerify(
                    workItem, payload.CurrentQuery, actionId, fromStateId, out var freshQuery, out var failureReason))
            {
                logger.LogError(
                    "Query push for work item {WorkItemId} (correlation {CorrelationId}) not attempted: {Reason}.",
                    workItem.Id, correlationId, failureReason);
                await RecordNotAttemptedAsync(
                    workItem, actionId, correlationId, failureReason, user, cancellationToken);
                return;
            }

            var queryNote = freshQuery.Reason;
            var sectionKeys = freshQuery.Sections;

            var result = await pushAdapter.PushQueryRaisedAsync(
                workItem.Id, correlationId, queryNote, sectionKeys, cancellationToken);

            var details = new Dictionary<string, string?>
            {
                ["actionId"] = actionId,
                ["sectionKeys"] = string.Join(",", sectionKeys),
                ["correlationId"] = correlationId.ToString(),
            };

            if (result.IsSuccess)
            {
                var appended = await auditAppender.AppendAsync(
                    workItem.Id, "query-push-sent", "Query pushed to operator backend", details, user, cancellationToken);
                if (!appended)
                {
                    logger.LogWarning(
                        "query-push-sent audit entry could not be persisted for work item {WorkItemId} (correlation {CorrelationId}).",
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
                    "Query push skipped for work item {WorkItemId} (correlation {CorrelationId}): {Reason}",
                    workItem.Id, correlationId, result.ErrorMessage);
                var appended = await auditAppender.AppendAsync(
                    workItem.Id, "query-push-skipped", "Query push to operator backend skipped", details, user, cancellationToken);
                if (!appended)
                {
                    logger.LogWarning(
                        "query-push-skipped audit entry could not be persisted for work item {WorkItemId} (correlation {CorrelationId}).",
                        workItem.Id, correlationId);
                }
            }
            else
            {
                details["errorMessage"] = result.ErrorMessage;
                // Promoted to LogError (was LogWarning): a failed push means
                // the operator backend's record has silently drifted from
                // ours, which should alert rather than blend into warning
                // noise.
                logger.LogError(
                    "Push of query-raised for work item {WorkItemId} (correlation {CorrelationId}) failed: {ErrorMessage}",
                    workItem.Id, correlationId, result.ErrorMessage);
                var appended = await auditAppender.AppendAsync(
                    workItem.Id, "query-push-failed", "Query push to operator backend failed", details, user, cancellationToken);
                if (!appended)
                {
                    // A lost failure record: nothing else on the work item
                    // says the operator's record has drifted, so it must be seen.
                    logger.LogError(
                        "query-push-failed audit entry could not be persisted for work item {WorkItemId} (correlation {CorrelationId}).",
                        workItem.Id, correlationId);
                }
            }
        }
        catch (Exception ex)
        {
            // Hooks must never throw — a push failure (or a failure to even
            // attempt the push, e.g. a payload deserialisation error) must
            // not unwind the already-persisted query transition.
            logger.LogError(
                ex, "Unexpected failure pushing query-raised for work item {WorkItemId} (correlation {CorrelationId}).",
                workItem.Id, correlationId);
        }
    }

    public Task OnAssignmentChangedAsync(
        WorkItem workItem, WorkItemAssignmentChange change, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// RA-000: records <c>query-push-failed</c> for a push that was never
    /// attempted because the hook could not prove what to send. Same outcome
    /// as a push the operator backend rejected (the operator's record has not
    /// caught up), told apart by <c>reason</c> instead of <c>errorMessage</c>.
    /// </summary>
    private async Task RecordNotAttemptedAsync(
        WorkItem workItem,
        string actionId,
        Guid correlationId,
        string reason,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string?>
        {
            ["actionId"] = actionId,
            ["correlationId"] = correlationId.ToString(),
            ["reason"] = reason,
        };
        var appended = await auditAppender.AppendAsync(
            workItem.Id, "query-push-failed", "Query push to operator backend failed", details, user, cancellationToken);
        if (!appended)
        {
            // A lost failure record, logged at Error like the rejected-push path.
            logger.LogError(
                "query-push-failed audit entry could not be persisted for work item {WorkItemId} (correlation {CorrelationId}).",
                workItem.Id, correlationId);
        }
    }
}