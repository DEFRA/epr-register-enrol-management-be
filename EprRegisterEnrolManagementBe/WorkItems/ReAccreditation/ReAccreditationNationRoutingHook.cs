using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-125: post-submission hook that derives the UK nation from the
/// re-accreditation payload's <c>SiteAddressPostcode</c> field, writes the
/// result back into <c>payload.Nation</c>, and records a
/// <c>routed-to-nation</c> audit entry.
///
/// All of this happens in a single <see cref="IWorkItemPersistence.ReplaceAsync"/>
/// so the payload update and the audit entry land atomically. Failures are
/// logged and swallowed so a transient DB hiccup does not unwind the
/// originating submission.
/// </summary>
internal sealed class ReAccreditationNationRoutingHook(
    INationResolver nationResolver,
    IWorkItemPersistence persistence,
    ILogger<ReAccreditationNationRoutingHook> logger,
    TimeProvider? timeProvider = null) : IWorkItemPostActionHook
{
    private const int MaxAttempts = 3;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task OnSubmittedAsync(
        WorkItem workItem,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (!IsReAccreditation(workItem))
        {
            return Task.CompletedTask;
        }

        return RouteAndRecordAsync(workItem, user, cancellationToken);
    }

    public Task OnActionAppliedAsync(
        WorkItem workItem,
        string actionId,
        string fromStateId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RouteAndRecordAsync(
        WorkItem workItem,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var postcode = ExtractPostcode(workItem);
        var nation = nationResolver.Resolve(postcode);
        var nationString = nation.ToString();

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var item = await persistence.GetByIdAsync(workItem.Id, cancellationToken);
            if (item is null)
            {
                logger.LogWarning(
                    "Nation routing skipped: work item {WorkItemId} not found.", workItem.Id);
                return;
            }

            // Stamp Nation into the payload BSON document.
            item.Payload["Nation"] = nationString;

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            item.AuditLog.Add(new WorkItemAuditEntry
            {
                Action = "routed-to-nation",
                ActionDisplayName = "Routed to nation",
                Details = new Dictionary<string, string?>
                {
                    ["nation"] = nationString,
                    ["derivedFrom"] = "site-address"
                },
                CreatedAt = now,
                CreatedBy = user.FindFirstValue("user:id"),
                CreatedByName = user.FindFirstValue("user:name")
            });

            try
            {
                await persistence.ReplaceAsync(item, cancellationToken);
                logger.LogInformation(
                    "Work item {WorkItemId} routed to nation {Nation} from postcode {Postcode}.",
                    workItem.Id, nationString, postcode ?? "(none)");
                return;
            }
            catch (WorkItemConcurrencyException)
            {
                if (attempt == MaxAttempts)
                {
                    logger.LogError(
                        "Nation routing for work item {WorkItemId} abandoned after {Attempts} attempts.",
                        workItem.Id, MaxAttempts);
                    return;
                }
            }
        }
    }

    private static bool IsReAccreditation(WorkItem workItem) =>
        string.Equals(workItem.TypeId, ReAccreditationType.Id, StringComparison.OrdinalIgnoreCase);

    private static string? ExtractPostcode(WorkItem workItem)
    {
        if (workItem.Payload is null || !workItem.Payload.Contains("SiteAddressPostcode"))
        {
            return null;
        }

        var element = workItem.Payload["SiteAddressPostcode"];
        return element.IsBsonNull ? null : element.AsString;
    }
}
