using System.Security.Claims;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Framework tenancy gate. Centralises the "can this caller see / mutate
/// this work item?" rule so the framework endpoints and module endpoints
/// (e.g. ReAccreditation) cannot drift out of sync. Modules MUST go through
/// this helper rather than re-implementing the role / SubmittedBy check
/// (epr-0t9, epr-946).
/// </summary>
public static class WorkItemTenancy
{
    /// <summary>
    /// True when <paramref name="user"/> is permitted to read or mutate
    /// <paramref name="workItem"/>: either they hold the case-worker role
    /// (which sees every tenant's items) or their cognito client id matches
    /// the item's <see cref="WorkItem.SubmittedBy"/>.
    /// </summary>
    public static bool CanRead(ClaimsPrincipal user, WorkItem workItem)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(workItem);

        if (user.IsInRole(WorkItemEndpoints.CaseWorkerRole)) return true;
        var callerClientId = user.FindFirstValue("cognito:client_id")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return callerClientId is not null
            && string.Equals(callerClientId, workItem.SubmittedBy, StringComparison.Ordinal);
    }
}
