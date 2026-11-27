using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

namespace EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;

/// <summary>
/// RA-698 review: reports where an add-ORS item sitting in the <c>updated</c>
/// waypoint was queried from (submitted, duly-made, assessment or decision),
/// derived from its own audit history by <see cref="StandardUpdatedOrigin"/>.
/// Without it the item reports <c>updated</c>, so a query raised while the item
/// was being duly made offers "Continue review" (which sends it back to
/// Submitted) and refuses "Duly make". Abstains outside <c>updated</c>, and when
/// the origin cannot be derived, so the engine falls back to the item's own state.
/// </summary>
internal sealed class AddOverseasSiteOriginStateResolver : IWorkItemOriginStateResolver
{
    public string TypeId => AddOverseasSiteType.Id;

    public string? ResolveOriginStateId(WorkItem workItem, IWorkItemTemplate template)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(template);

        return
            string.Equals(workItem.TypeId, AddOverseasSiteType.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(workItem.StateId, StandardUpdatedOrigin.StateId, StringComparison.OrdinalIgnoreCase)
            ? StandardUpdatedOrigin.ResolveOriginatingStateId(workItem, template)
            : null;
    }
}
