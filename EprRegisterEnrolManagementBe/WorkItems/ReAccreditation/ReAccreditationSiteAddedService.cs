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
/// so the only work here (for now) is confirming the work item exists and is
/// a re-accreditation item. RA102-cx9 adds the <c>site-added</c> audit-log
/// side effect on top of this scaffold.
/// </summary>
internal sealed class ReAccreditationSiteAddedService(IWorkItemPersistence persistence)
    : IReAccreditationSiteAddedService
{
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

        return WorkItemActionResult.Success(workItem);
    }
}
