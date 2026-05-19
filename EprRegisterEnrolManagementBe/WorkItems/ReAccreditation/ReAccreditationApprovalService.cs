using System.Security.Claims;
using EprRegisterEnrolManagementBe.Utils.Background;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-132 default <see cref="IReAccreditationApprovalService"/>.
///
/// On a single <see cref="IWorkItemPersistence.ReplaceAsync"/> call the
/// service:
/// <list type="bullet">
///   <item>Validates the work item exists, is a re-accreditation, is
///   readable by the caller, is in <c>assessment-in-progress</c>, and that
///   the caller holds the decision-maker role.</item>
///   <item>Stamps a fresh accreditation id, today's date as the
///   <c>AccreditationStartDate</c>, and a non-null
///   <see cref="SlaClock.StoppedAt"/> on the payload.</item>
///   <item>Transitions <c>StateId</c> to <c>approved</c>.</item>
///   <item>Appends three audit entries in order: <c>approved</c> (the
///   action-applied entry), <c>sla-clock-stopped</c>, and
///   <c>accreditation-issued</c>.</item>
/// </list>
///
/// On a <see cref="WorkItemConcurrencyException"/> the operation is
/// retried by reloading the latest document and re-running validation,
/// up to <see cref="MaxAttempts"/> times — mirroring the
/// <c>WorkItemAuditAppender</c> retry pattern.
///
/// After success the service enqueues a background job that appends a
/// <c>publishing-enqueued</c> audit entry via a scoped
/// <see cref="IWorkItemAuditAppender"/>, then invokes the registered
/// <see cref="IWorkItemPostActionHook"/>s with action id
/// <c>approve</c> so the notification hook fires the decision email.
/// </summary>
internal sealed class ReAccreditationApprovalService(
    IWorkItemPersistence persistence,
    IAccreditationIdGenerator accreditationIdGenerator,
    IBackgroundTaskQueue backgroundTaskQueue,
    IEnumerable<IWorkItemPostActionHook> postActionHooks,
    ILogger<ReAccreditationApprovalService> logger,
    TimeProvider? timeProvider = null) : IReAccreditationApprovalService
{
    private const int MaxAttempts = 3;
    private const string FromStateId = "assessment-in-progress";
    private const string ToStateId = "approved";
    private const string ActionId = "approve";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IWorkItemPostActionHook[] _postActionHooks = postActionHooks.ToArray();

    public async Task<WorkItemActionResult> ApproveAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        WorkItem? approved = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);

            // Cross-tenant gate (epr-946 contract): hide existence from
            // callers that cannot read the document.
            if (workItem is null || !WorkItemTenancy.CanRead(user, workItem))
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

            if (string.Equals(workItem.StateId, ToStateId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(workItem.StateId, "rejected", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(workItem.StateId, "withdrawn", StringComparison.OrdinalIgnoreCase))
            {
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.TerminalState,
                    $"Work item {workItemId} is in terminal state '{workItem.StateId}'; no actions are allowed.");
            }

            if (!string.Equals(workItem.StateId, FromStateId, StringComparison.OrdinalIgnoreCase))
            {
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.InvalidTransition,
                    $"Action '{ActionId}' moves work items from '{FromStateId}', " +
                    $"but {workItemId} is in '{workItem.StateId}'.");
            }

            if (!user.IsInRole(ReAccreditationType.DecisionMakerRole))
            {
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.NotAuthorized,
                    $"Action '{ActionId}' requires the '{ReAccreditationType.DecisionMakerRole}' role.");
            }

            var now = _timeProvider.GetUtcNow();
            var nowUtc = now.UtcDateTime;
            var accreditationId = accreditationIdGenerator.Generate();
            var accreditationStartDate = DateOnly.FromDateTime(nowUtc);

            if (!TryApplyApprovalToPayload(workItem, accreditationId, accreditationStartDate, now))
            {
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.InvalidTransition,
                    $"Work item '{workItemId}' payload is corrupt and cannot be read. " +
                    "Inspect the server logs for details; a manual data repair may be required.");
            }

            var previousState = workItem.StateId;
            workItem.StateId = ToStateId;
            workItem.LastModifiedAt = nowUtc;

            var auditDetails = new Dictionary<string, string?>
            {
                ["actionId"] = ActionId,
                ["actionDisplayName"] = "Approve",
                ["fromStateId"] = previousState,
                ["toStateId"] = workItem.StateId
            };
            AppendAudit(workItem, "action-applied", "Action applied", user, nowUtc, auditDetails);
            AppendAudit(workItem, "sla-clock-stopped", "SLA clock stopped", user, nowUtc, new()
            {
                ["stoppedAt"] = now.ToString("O")
            });
            AppendAudit(workItem, "accreditation-issued", "Accreditation issued", user, nowUtc, new()
            {
                ["accreditationId"] = accreditationId,
                ["startDate"] = accreditationStartDate.ToString("yyyy-MM-dd")
            });

            try
            {
                await persistence.ReplaceAsync(workItem, cancellationToken);
                approved = workItem;
                break;
            }
            catch (WorkItemConcurrencyException)
            {
                if (attempt == MaxAttempts)
                {
                    logger.LogError(
                        "ReAccreditation approval for work item {WorkItemId} abandoned after {Attempts} attempts " +
                        "due to repeated concurrency conflicts.", workItemId, MaxAttempts);
                    return WorkItemActionResult.Failure(
                        WorkItemActionFailureCode.ConcurrencyConflict,
                        $"Work item '{workItemId}' was modified concurrently. Reload the work item and retry.");
                }
            }
        }

        // Loop has either returned (validation failure / exhausted retries)
        // or assigned `approved`. The compiler does not see the latter so
        // narrow with an assertion.
        var persisted = approved!;

        logger.LogInformation(
            "Re-accreditation work item {WorkItemId} approved by {UserId}",
            persisted.Id, user.FindFirstValue("user:id"));

        await EnqueuePublishingAuditAsync(persisted, user, cancellationToken);
        await InvokeActionAppliedHooksAsync(persisted, ActionId, FromStateId, user, cancellationToken);

        return WorkItemActionResult.Success(persisted);
    }

    private bool TryApplyApprovalToPayload(
        WorkItem workItem,
        string accreditationId,
        DateOnly accreditationStartDate,
        DateTimeOffset stoppedAt)
    {
        // Deserialise → with-mutate → reserialise so the mongo-driver's
        // serializer (camelCase convention, nested record) is the single
        // source of truth for the on-disk shape.
        ReAccreditationPayload payload;
        try
        {
            payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload ?? new BsonDocument());
        }
        catch (Exception ex) when (ex is BsonSerializationException or FormatException or InvalidCastException)
        {
            logger.LogError(ex,
                "Re-accreditation approval for work item {WorkItemId} aborted: " +
                "existing payload could not be deserialised. Approving would destroy " +
                "existing payload data (organisation name, registration number, nation, etc.).",
                workItem.Id);
            return false;
        }

        var updated = payload with
        {
            AccreditationId = accreditationId,
            AccreditationStartDate = accreditationStartDate,
            SlaClock = new SlaClock(stoppedAt)
        };

        workItem.ReplacePayload(updated.ToBsonDocument());
        return true;
    }

    private async Task EnqueuePublishingAuditAsync(
        WorkItem workItem,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        // ClaimsPrincipal holds only in-memory state, so it is safe to
        // close over and hand to the background job; the queued
        // delegate runs on its own DI scope.
        var workItemId = workItem.Id;
        var accreditationIdValue = TryReadString(workItem.Payload, "accreditationId");

        await backgroundTaskQueue.QueueAsync(async (scopedServices, ct) =>
        {
            var appender = scopedServices.GetRequiredService<IWorkItemAuditAppender>();
            await appender.AppendAsync(
                workItemId,
                action: "publishing-enqueued",
                actionDisplayName: "Publishing enqueued",
                details: new Dictionary<string, string?>
                {
                    ["accreditationId"] = accreditationIdValue
                },
                user,
                ct);
        }, cancellationToken);
    }

    private async Task InvokeActionAppliedHooksAsync(
        WorkItem workItem,
        string actionId,
        string fromStateId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        foreach (var hook in _postActionHooks)
        {
            try
            {
                await hook.OnActionAppliedAsync(workItem, actionId, fromStateId, user, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Post-action transition hook {HookType} failed for work item {WorkItemId} action {ActionId}",
                    hook.GetType().FullName, workItem.Id, actionId);
            }
        }
    }

    private static void AppendAudit(
        WorkItem workItem,
        string action,
        string actionDisplayName,
        ClaimsPrincipal user,
        DateTime createdAt,
        Dictionary<string, string?> details)
    {
        workItem.AuditLog.Add(new WorkItemAuditEntry
        {
            Action = action,
            ActionDisplayName = actionDisplayName,
            Details = details,
            CreatedAt = createdAt,
            CreatedBy = user.FindFirstValue("user:id"),
            CreatedByName = user.FindFirstValue("user:name")
        });
    }

    private static WorkItemActionResult? RequireActorIdentity(ClaimsPrincipal user)
    {
        var id = user.FindFirstValue("user:id");
        if (!string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        return WorkItemActionResult.Failure(
            WorkItemActionFailureCode.MissingActorIdentity,
            "Mutating this work item requires an authenticated end user; " +
            "the request did not include a 'user:id' claim.");
    }

    private static string? TryReadString(BsonDocument? document, string fieldName)
    {
        if (document is null || !document.TryGetValue(fieldName, out var value))
        {
            return null;
        }
        return value.IsBsonNull ? null : value.ToString();
    }
}
