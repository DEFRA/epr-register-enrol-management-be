using System.Globalization;
using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;

/// <summary>
/// RA-698: post-action hook that pushes the regulator's query note to the
/// operator backend (<c>POST api/v1/accreditation-changes/case-management/{id}/query</c>,
/// via <see cref="IOperatorBackendPushAdapter.PushChangeRequestQueryRaisedAsync"/>)
/// whenever an add-ORS item is queried, so the operator journey can show the
/// query and open the item for a response. Generalised from
/// <c>ReAccreditationQueryPushHook</c>, with the same outcomes recorded as
/// <c>query-push-sent</c>, <c>query-push-skipped</c> (push disabled, never an
/// alert) and <c>query-push-failed</c>.
///
/// Reads the open query from <c>payload.currentQuery</c>, which
/// <see cref="AddOverseasSiteQueryService"/> stamps before the transition. As
/// defence in depth, it pushes nothing, logs at Error and records
/// <c>query-push-failed</c> with a <c>reason</c> when the stamp is missing or
/// was not raised inside this transition's window (<see cref="TryVerify"/>).
/// Never throws: a push failure must not unwind the persisted transition.
/// </summary>
internal sealed class AddOverseasSiteQueryPushHook(
    IOperatorBackendPushAdapter pushAdapter,
    IWorkItemAuditAppender auditAppender,
    ILogger<AddOverseasSiteQueryPushHook> logger
) : IWorkItemPostActionHook
{
    private const string ActionAppliedAuditAction = "action-applied";

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
        CancellationToken cancellationToken
    )
    {
        if (
            !string.Equals(workItem.TypeId, AddOverseasSiteType.Id, StringComparison.OrdinalIgnoreCase)
            || !s_queryActionIds.Contains(actionId)
        )
        {
            return;
        }

        // One correlation id per push, threaded through the HTTP header, the
        // logs and the audit entries so both services' logs can be joined.
        var correlationId = Guid.NewGuid();

        try
        {
            if (!TryVerify(workItem, actionId, fromStateId, out var note, out var raisedAt, out var failureReason))
            {
                logger.LogError(
                    "Query push for work item {WorkItemId} (correlation {CorrelationId}) not attempted: {Reason}.",
                    workItem.Id,
                    correlationId,
                    failureReason
                );
                await RecordFailedAsync(
                    workItem,
                    correlationId,
                    NewDetails(actionId, correlationId, ("reason", failureReason)),
                    user,
                    cancellationToken
                );
                return;
            }

            var result = await pushAdapter.PushChangeRequestQueryRaisedAsync(
                workItem.Id,
                correlationId,
                note,
                raisedAt,
                cancellationToken
            );
            var details = NewDetails(actionId, correlationId);

            if (result.IsSuccess)
            {
                await AppendAsync(
                    workItem,
                    correlationId,
                    "query-push-sent",
                    "Query pushed to operator backend",
                    details,
                    user,
                    cancellationToken
                );
            }
            else if (result.IsSkipped)
            {
                details["reason"] = result.ErrorMessage;
                logger.LogDebug(
                    "Query push skipped for work item {WorkItemId} (correlation {CorrelationId}): {Reason}",
                    workItem.Id,
                    correlationId,
                    result.ErrorMessage
                );
                await AppendAsync(
                    workItem,
                    correlationId,
                    "query-push-skipped",
                    "Query push to operator backend skipped",
                    details,
                    user,
                    cancellationToken
                );
            }
            else
            {
                details["errorMessage"] = result.ErrorMessage;
                logger.LogError(
                    "Push of query-raised for work item {WorkItemId} (correlation {CorrelationId}) failed: {ErrorMessage}",
                    workItem.Id,
                    correlationId,
                    result.ErrorMessage
                );
                await RecordFailedAsync(workItem, correlationId, details, user, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Hooks must never throw: the query transition is already persisted.
            logger.LogError(
                ex,
                "Unexpected failure pushing query-raised for work item {WorkItemId} (correlation {CorrelationId}).",
                workItem.Id,
                correlationId
            );
        }
    }

    public Task OnAssignmentChangedAsync(
        WorkItem workItem,
        WorkItemAssignmentChange change,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    ) => Task.CompletedTask;

    /// <summary>
    /// Reads <c>payload.currentQuery</c> and checks it was raised inside this
    /// transition's window: no earlier than the item entered
    /// <paramref name="fromStateId"/> (so it is not an earlier query's) and no
    /// later than the transition. The engine appends the transition's
    /// <c>action-applied</c> entry before running the hooks, so it is the last
    /// one on the instance the hook is handed.
    /// </summary>
    internal static bool TryVerify(
        WorkItem workItem,
        string actionId,
        string fromStateId,
        out string note,
        out DateTime raisedAt,
        out string failureReason
    )
    {
        note = string.Empty;
        raisedAt = default;
        failureReason = string.Empty;

        if (
            !workItem.Payload.TryGetValue(AddOverseasSiteQueryService.CurrentQueryPayloadField, out var value)
            || value is not BsonDocument current
        )
        {
            failureReason = "payload.currentQuery is missing";
            return false;
        }

        if (!current.TryGetValue("raisedAt", out var raisedAtValue) || !raisedAtValue.IsValidDateTime)
        {
            failureReason = "payload.currentQuery.raisedAt is missing";
            return false;
        }

        if (!current.TryGetValue("reason", out var reasonValue) || !reasonValue.IsString)
        {
            failureReason = "payload.currentQuery.reason is missing";
            return false;
        }

        var transitionIndex = workItem.AuditLog.FindLastIndex(IsActionApplied);
        if (transitionIndex < 0 || !IsEntryFor(workItem.AuditLog[transitionIndex], actionId, fromStateId))
        {
            failureReason =
                $"the audit log has no action-applied entry for {actionId} from '{fromStateId}' to 'queried'";
            return false;
        }

        var raised = raisedAtValue.ToUniversalTime();
        var transitionAt = workItem.AuditLog[transitionIndex].CreatedAt;
        var previousIndex =
            transitionIndex == 0 ? -1 : workItem.AuditLog.FindLastIndex(transitionIndex - 1, transitionIndex, IsActionApplied);
        var enteredFromStateAt = previousIndex < 0 ? workItem.SubmittedAt : workItem.AuditLog[previousIndex].CreatedAt;

        if (raised < enteredFromStateAt)
        {
            failureReason =
                $"payload.currentQuery was raised at {Format(raised)}, before the work item entered '{fromStateId}' "
                + $"at {Format(enteredFromStateAt)}, so it belongs to an earlier query";
            return false;
        }

        if (raised > transitionAt)
        {
            failureReason =
                $"payload.currentQuery was raised at {Format(raised)}, after the {actionId} transition at {Format(transitionAt)}";
            return false;
        }

        note = reasonValue.AsString;
        raisedAt = raised;
        return true;
    }

    private static bool IsActionApplied(WorkItemAuditEntry entry) =>
        string.Equals(entry.Action, ActionAppliedAuditAction, StringComparison.Ordinal);

    private static bool IsEntryFor(WorkItemAuditEntry entry, string actionId, string fromStateId) =>
        string.Equals(entry.Details.GetValueOrDefault("actionId"), actionId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(entry.Details.GetValueOrDefault("fromStateId"), fromStateId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(entry.Details.GetValueOrDefault("toStateId"), "queried", StringComparison.OrdinalIgnoreCase);

    private static string Format(DateTime value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static Dictionary<string, string?> NewDetails(
        string actionId,
        Guid correlationId,
        params (string Key, string? Value)[] extra
    )
    {
        var details = new Dictionary<string, string?>
        {
            ["actionId"] = actionId,
            ["correlationId"] = correlationId.ToString(),
        };
        foreach (var (key, value) in extra)
        {
            details[key] = value;
        }

        return details;
    }

    /// <summary>
    /// Records <c>query-push-failed</c>: a push the operator backend rejected,
    /// or one never attempted because the hook could not establish what to
    /// send. A failure record that cannot be persisted is lost, so that is
    /// logged at Error.
    /// </summary>
    private Task RecordFailedAsync(
        WorkItem workItem,
        Guid correlationId,
        Dictionary<string, string?> details,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    ) =>
        AppendAsync(
            workItem,
            correlationId,
            "query-push-failed",
            "Query push to operator backend failed",
            details,
            user,
            cancellationToken
        );

    private async Task AppendAsync(
        WorkItem workItem,
        Guid correlationId,
        string action,
        string displayName,
        Dictionary<string, string?> details,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (!await auditAppender.AppendAsync(workItem.Id, action, displayName, details, user, cancellationToken))
        {
            // A failure record that cannot be persisted is lost, so that is logged at Error.
            logger.Log(
                action == "query-push-failed" ? LogLevel.Error : LogLevel.Warning,
                "{Action} audit entry could not be persisted for work item {WorkItemId} (correlation {CorrelationId}).",
                action,
                workItem.Id,
                correlationId
            );
        }
    }
}
