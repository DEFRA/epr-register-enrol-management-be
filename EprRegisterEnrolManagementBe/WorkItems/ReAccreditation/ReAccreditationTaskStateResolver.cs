using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-372: makes the <c>updated</c> waypoint show the review that is actually
/// outstanding.
///
/// The bug this fixes: a regulator queries an application mid-review, the
/// operator updates it, the application lands in <c>updated</c> — and the
/// task list goes empty, because <c>updated</c> declares no tasks of its own.
/// The regulator has no way to finish the review and no sight of the progress
/// they had already made.
///
/// The fix is to say that while an item is in <c>updated</c>, the tasks that
/// apply are the tasks of the state the query was raised from. Because the
/// engine stores task completion per state id, redirecting the state id
/// redirects reads and writes together: work done before the query is still
/// shown as done, and work done during the detour is still done once
/// <c>continue-review-during-*</c> carries the item back. No new storage, and
/// no template bump — every in-flight item already carries a snapshot with the
/// originating state's tasks in it.
///
/// Scope is deliberately narrow. <c>queried</c> is left alone: an application
/// awaiting a response from the operator has nothing for the regulator to
/// work on, so an empty list there is correct, not a bug.
/// </summary>
internal sealed class ReAccreditationTaskStateResolver : IWorkItemTaskStateResolver
{
    public string? ResolveTaskStateId(WorkItem workItem, IWorkItemTemplate template)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(template);

        // Abstain for every other type and every other state — including
        // 'queried' — so the engine falls back to the item's own state and
        // this resolver stays invisible outside the one case it exists for.
        if (!ReAccreditationUpdatedOrigin.IsUpdatedReAccreditation(workItem))
        {
            return null;
        }

        // Null when the originating state cannot be determined (an item whose
        // frozen snapshot predates the continue-review-during-* transitions).
        // Abstaining leaves it with the pre-RA-372 empty task list, which is
        // wrong but harmless, rather than guessing a state and showing a
        // regulator someone else's checklist.
        return ReAccreditationUpdatedOrigin.ResolveOriginatingStateId(workItem, template);
    }
}
