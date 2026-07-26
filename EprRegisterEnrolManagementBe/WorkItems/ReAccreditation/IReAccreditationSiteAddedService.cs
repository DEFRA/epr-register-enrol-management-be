using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-294/RA-297: records that the operator backend added a new ORS or
/// interim site to a re-accreditation application. Deliberately thin — the
/// only side effect is a <c>site-added</c> audit-log entry via
/// <see cref="IWorkItemAuditAppender"/>; this repo never models ORS/
/// interim-site detail itself (see <see cref="WorkItem.Payload"/>).
/// </summary>
internal interface IReAccreditationSiteAddedService
{
    Task<WorkItemActionResult> RecordSiteAddedAsync(
        Guid workItemId,
        SiteAddedRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );
}
