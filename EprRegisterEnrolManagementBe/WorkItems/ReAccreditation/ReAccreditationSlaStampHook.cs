using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Post-action hook that stamps the SLA clock the moment the
/// <c>payment-received</c> transition is applied. The clock's
/// <c>StartedAt</c> is set to the current UTC time so the nightly
/// <see cref="SlaBreachBackgroundService"/> can evaluate breach status
/// from the next run onwards.
///
/// A concurrency conflict on the follow-up <c>ReplaceAsync</c> is logged
/// and swallowed — the same pattern used by
/// <see cref="ReAccreditationNationRoutingHook"/> — so a transient write
/// failure never rolls back the originating transition.
/// </summary>
internal sealed class ReAccreditationSlaStampHook(
    IWorkItemPersistence persistence,
    TimeProvider timeProvider,
    ILogger<ReAccreditationSlaStampHook> logger) : IWorkItemPostActionHook
{
    public Task OnSubmittedAsync(WorkItem workItem, ClaimsPrincipal user, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public async Task OnActionAppliedAsync(
        WorkItem workItem,
        string actionId,
        string fromStateId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(workItem.TypeId, ReAccreditationType.Id, StringComparison.OrdinalIgnoreCase))
            return;

        if (!string.Equals(actionId, "payment-received", StringComparison.OrdinalIgnoreCase))
            return;

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Re-fetch the document so we use the version the engine just wrote
        // rather than an in-memory reference that may be stale if another
        // hook called persistence.ReplaceAsync or auditAppender.AppendAsync
        // before us (both advance the DB version independently).
        var fresh = await persistence.GetByIdAsync(workItem.Id, cancellationToken);
        if (fresh is null)
        {
            logger.LogWarning(
                "SLA stamp skipped: work item {WorkItemId} not found on re-fetch.", workItem.Id);
            return;
        }

        fresh.SlaClock = new WorkItemSlaClock { StartedAt = now };
        fresh.LastModifiedAt = now;

        try
        {
            await persistence.ReplaceAsync(fresh, cancellationToken);
            logger.LogInformation(
                "SLA clock started for work item {WorkItemId} at {StartedAt}.", workItem.Id, now);
        }
        catch (WorkItemConcurrencyException)
        {
            logger.LogWarning(
                "Concurrency conflict stamping SLA clock for {WorkItemId}; clock may not be persisted.",
                workItem.Id);
        }
    }
}
