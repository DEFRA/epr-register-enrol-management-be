using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-294/RA-297 default <see cref="IReAccreditationSiteAddedService"/>.
///
/// Called by the operator backend whenever an operator adds a new ORS or
/// interim site to an accreditation application. There is no state
/// transition — the application's lifecycle is unaffected by adding a site —
/// so the only work here is (a) confirming the work item exists and is a
/// re-accreditation item, and (b) appending a <c>site-added</c> audit entry
/// carrying the request's fields verbatim, mirroring
/// <see cref="ReAccreditationQueryPushHook"/>'s own
/// "receive an external signal, append an audit entry, never let the append
/// step throw" shape. No <c>user:id</c> claim is required: this is a
/// system-to-system notification, not a case-worker action, so a missing
/// forwarded user identity simply means the audit entry's
/// <c>CreatedBy</c>/<c>CreatedByName</c> are null — the same "system entry"
/// convention <see cref="ReAccreditationPaymentService"/> uses for its own
/// notification-outcome audit entries.
/// </summary>
internal sealed class ReAccreditationSiteAddedService(
    IWorkItemPersistence persistence,
    IWorkItemAuditAppender auditAppender,
    ILogger<ReAccreditationSiteAddedService> logger
) : IReAccreditationSiteAddedService
{
    /// <summary>Audit action id for the RA-294/RA-297 site-added entry.</summary>
    public const string AuditAction = "site-added";

    public const string AuditActionDisplayName = "Site added";

    public async Task<WorkItemActionResult> RecordSiteAddedAsync(
        Guid workItemId,
        SiteAddedRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);

        var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."
            );
        }

        if (
            !string.Equals(
                workItem.TypeId,
                ReAccreditationType.Id,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                $"Work item {workItemId} is of type '{workItem.TypeId}', not '{ReAccreditationType.Id}'."
            );
        }

        var details = new Dictionary<string, string?>
        {
            ["siteType"] = request.SiteType,
            ["orsId"] = request.OrsId,
            ["siteNumber"] = request.SiteNumber,
            ["isNewSite"] = request.IsNewSite.ToString(),
        };

        bool appended;
        try
        {
            appended = await auditAppender.AppendAsync(
                workItemId,
                AuditAction,
                AuditActionDisplayName,
                details,
                user,
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            // Never let the append step throw: this endpoint's entire purpose
            // is the audit entry, but an unexpected failure while writing it
            // must surface as a controlled failure result, not an unhandled
            // 500, for the same reason ReAccreditationQueryPushHook never lets
            // a push/append failure propagate.
            logger.LogError(
                ex,
                "Unexpected failure appending site-added audit entry for work item {WorkItemId}.",
                workItemId
            );
            appended = false;
        }

        if (!appended)
        {
            logger.LogWarning(
                "site-added audit entry could not be persisted for work item {WorkItemId}.",
                workItemId
            );
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.ConcurrencyConflict,
                "Could not append the site-added audit entry."
            );
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Recorded site-added notification for work item {WorkItemId}: siteType={SiteType}, "
                    + "orsId={OrsId}, siteNumber={SiteNumber}, isNewSite={IsNewSite}.",
                workItemId,
                request.SiteType,
                request.OrsId,
                request.SiteNumber,
                request.IsNewSite
            );
        }

        // Re-read so the response carries the site-added audit entry the
        // out-of-band appender wrote against its own copy of the document.
        var refreshed = await persistence.GetByIdAsync(workItemId, cancellationToken);
        return refreshed is null
            ? WorkItemActionResult.Success(workItem)
            : WorkItemActionResult.Success(refreshed);
    }
}
