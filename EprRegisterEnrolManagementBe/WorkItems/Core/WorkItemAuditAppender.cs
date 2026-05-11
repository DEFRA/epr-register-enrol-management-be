using System.Security.Claims;
using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Default <see cref="IWorkItemAuditAppender"/>. Re-loads the work item
/// from <see cref="IWorkItemPersistence"/> before each save so it picks
/// up any audit entries written by the engine immediately before the
/// hook fires.
/// </summary>
internal sealed class WorkItemAuditAppender(
    IWorkItemPersistence persistence,
    ILogger<WorkItemAuditAppender> logger,
    TimeProvider? timeProvider = null) : IWorkItemAuditAppender
{
    private const int MaxAttempts = 3;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<bool> AppendAsync(
        Guid workItemId,
        string action,
        string actionDisplayName,
        Dictionary<string, string?> details,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);
            if (workItem is null)
            {
                logger.LogWarning(
                    "Out-of-band audit append skipped: work item {WorkItemId} not found.", workItemId);
                return false;
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            workItem.AuditLog.Add(new WorkItemAuditEntry
            {
                Action = action,
                ActionDisplayName = actionDisplayName,
                Details = details,
                CreatedAt = now,
                CreatedBy = user.FindFirstValue("user:id"),
                CreatedByName = user.FindFirstValue("user:name")
            });

            try
            {
                await persistence.ReplaceAsync(workItem, cancellationToken);
                return true;
            }
            catch (WorkItemConcurrencyException)
            {
                if (attempt == MaxAttempts)
                {
                    logger.LogError(
                        "Out-of-band audit append for work item {WorkItemId} abandoned after {Attempts} attempts.",
                        workItemId, MaxAttempts);
                    return false;
                }
            }
        }

        return false;
    }
}
