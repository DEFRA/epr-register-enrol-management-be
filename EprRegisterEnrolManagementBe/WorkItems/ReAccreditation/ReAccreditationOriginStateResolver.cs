using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Tells callers where an application sitting in the <c>updated</c> waypoint
/// is headed back to.
///
/// A regulator queries an application mid-review, the operator responds, and
/// the application lands in <c>updated</c> (RA-337). <c>updated</c> is not a
/// destination — it exists so a caseworker can see that a response has
/// arrived — and the only useful thing to say about an item there is which
/// stage of the workflow it came from and will return to. That is derivable
/// only from the work item's own audit history, so no client can compute it:
/// the case management frontend uses it to decide which call to action to
/// offer, most importantly whether to offer "Duly make" for an application
/// queried out of <c>submitted</c> while refusing one queried out of
/// assessment or decision, where offering it would invite a caseworker to
/// send the application backwards.
///
/// RA-410: this was <c>ReAccreditationTaskStateResolver</c>, which answered
/// the same question in order to pick a task checklist. The checklists are
/// gone; the question is not. It now feeds
/// <see cref="WorkItemResponse.OriginStateId"/> as a pure read projection and
/// gates nothing.
///
/// Scope is deliberately narrow. <c>queried</c> is left alone: an application
/// still awaiting a response from the operator has not been resubmitted, so
/// there is nothing to route onwards yet.
/// </summary>
internal sealed class ReAccreditationOriginStateResolver : IWorkItemOriginStateResolver
{
    public string TypeId => ReAccreditationType.Id;

    public string? ResolveOriginStateId(WorkItem workItem, IWorkItemTemplate template)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(template);

        // Abstain for every state other than the waypoint — including
        // 'queried' — so the engine falls back to the item's own state and
        // this resolver stays invisible outside the one case it exists for.
        // The type half of this check is now redundant with the engine's own
        // TypeId scoping, but is kept as defence in depth: this method is
        // public and exercised directly by unit tests.
        if (!ReAccreditationUpdatedOrigin.IsUpdatedReAccreditation(workItem))
        {
            return null;
        }

        // Null when the originating state cannot be determined (an item whose
        // frozen snapshot predates the continue-review-during-* transitions).
        // Abstaining makes the engine fall back to 'updated' itself, which
        // correctly refuses every origin-specific call to action, rather than
        // guessing a state and offering one that would move the application
        // backwards.
        return ReAccreditationUpdatedOrigin.ResolveOriginatingStateId(workItem, template);
    }
}
