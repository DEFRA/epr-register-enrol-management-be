using System.Security.Claims;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Out-of-band audit-log appender used by post-action hooks (e.g. the
/// re-accreditation notification hook) to record their own outcome on
/// the persisted <see cref="WorkItem"/>. Loads the latest copy,
/// appends an entry, and saves with one optimistic-concurrency retry
/// so a race with another writer does not silently drop the entry.
/// </summary>
public interface IWorkItemAuditAppender
{
    /// <summary>
    /// Append a single <see cref="WorkItemAuditEntry"/> to the work
    /// item identified by <paramref name="workItemId"/>. Returns
    /// <c>true</c> when the entry was persisted; <c>false</c> when the
    /// work item could not be found or the write was abandoned after
    /// retries.
    /// </summary>
    Task<bool> AppendAsync(
        Guid workItemId,
        string action,
        string actionDisplayName,
        Dictionary<string, string?> details,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);
}
