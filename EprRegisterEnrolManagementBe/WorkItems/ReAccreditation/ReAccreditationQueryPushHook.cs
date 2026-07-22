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
/// Never throws — a push failure must not unwind the already-persisted
/// query transition (the <see cref="IWorkItemPostActionHook"/> contract).
/// Records the outcome as a <c>query-push-sent</c> / <c>query-push-failed</c>
/// audit entry, mirroring <see cref="ReAccreditationNotificationHook"/>'s
/// own <c>notification-sent</c>/<c>notification-failed</c> pattern. A failed
/// audit append is logged, not retried.
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

        try
        {
            var payload = DeserialisePayload(workItem);
            var queryNote = payload?.CurrentQuery?.Reason ?? string.Empty;
            var sectionKeys = payload?.CurrentQuery?.Sections ?? [];

            var result = await pushAdapter.PushQueryRaisedAsync(workItem.Id, queryNote, sectionKeys, cancellationToken);

            var details = new Dictionary<string, string?>
            {
                ["actionId"] = actionId,
                ["sectionKeys"] = string.Join(",", sectionKeys),
            };

            if (result.IsSuccess)
            {
                var appended = await auditAppender.AppendAsync(
                    workItem.Id, "query-push-sent", "Query pushed to operator backend", details, user, cancellationToken);
                if (!appended)
                {
                    logger.LogWarning(
                        "query-push-sent audit entry could not be persisted for work item {WorkItemId}.", workItem.Id);
                }
            }
            else
            {
                details["errorMessage"] = result.ErrorMessage;
                logger.LogWarning(
                    "Push of query-raised for work item {WorkItemId} failed: {ErrorMessage}",
                    workItem.Id, result.ErrorMessage);
                var appended = await auditAppender.AppendAsync(
                    workItem.Id, "query-push-failed", "Query push to operator backend failed", details, user, cancellationToken);
                if (!appended)
                {
                    logger.LogWarning(
                        "query-push-failed audit entry could not be persisted for work item {WorkItemId}.", workItem.Id);
                }
            }
        }
        catch (Exception ex)
        {
            // Hooks must never throw — a push failure (or a failure to even
            // attempt the push, e.g. a payload deserialisation error) must
            // not unwind the already-persisted query transition.
            logger.LogError(ex, "Unexpected failure pushing query-raised for work item {WorkItemId}.", workItem.Id);
        }
    }

    public Task OnAssignmentChangedAsync(
        WorkItem workItem, WorkItemAssignmentChange change, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private ReAccreditationPayload? DeserialisePayload(WorkItem workItem)
    {
        try
        {
            return BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Failed to deserialise payload for work item {WorkItemId}; query push will be skipped.", workItem.Id);
            return null;
        }
    }
}
