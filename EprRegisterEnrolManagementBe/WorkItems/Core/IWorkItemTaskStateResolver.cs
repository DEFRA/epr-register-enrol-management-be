namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-372: module-supplied seam that lets a work item type say "the tasks
/// that apply to this item right now are the tasks of some state other than
/// the one it is sitting in".
///
/// The engine normally treats a work item's <see cref="WorkItem.StateId"/> as
/// both "where the item is" and "whose task list applies". For most types
/// those are the same thing. They come apart for a waypoint state — a state
/// an item passes through without having a task list of its own, where the
/// work still outstanding belongs to the state the item came from and will
/// return to. The re-accreditation module's <c>updated</c> state (RA-337) is
/// the motivating case: an application queried mid-review lands there once
/// the operator responds, and the regulator has to be able to finish the
/// review tasks of whichever state the query was raised from.
///
/// Keeping this a resolver rather than a rule in the engine is the point.
/// Core must not know that a state called <c>updated</c> exists, or that
/// <c>resume-during-*</c> actions mean anything — other types have no such
/// concept. Core only knows that a type may have an opinion about which
/// state's tasks apply, and asks.
///
/// Implementations must:
/// <list type="bullet">
///   <item>Return <c>null</c> when they have no opinion — including for every
///   work item whose <see cref="WorkItem.TypeId"/> is not their own. The
///   engine falls back to <see cref="WorkItem.StateId"/>, so a resolver that
///   abstains is invisible.</item>
///   <item>Be pure and side-effect free. This runs on every read projection
///   and on every task mutation, so it must not perform I/O.</item>
///   <item>Resolve against the supplied <paramref name="template"/> — the
///   work item's own frozen snapshot — rather than the live type, so an
///   in-flight item is judged by the rules it was submitted under.</item>
/// </list>
/// </summary>
public interface IWorkItemTaskStateResolver
{
    /// <summary>
    /// The id of the state whose task list applies to <paramref name="workItem"/>,
    /// or <c>null</c> to defer to <see cref="WorkItem.StateId"/>.
    ///
    /// The returned id governs the task list the engine projects AND the
    /// per-state bucket task completions are read from and written to, so a
    /// resolver that redirects to another state redirects both halves
    /// consistently — progress recorded before the detour is still visible,
    /// and progress recorded during it survives the return.
    /// </summary>
    /// <param name="workItem">The work item being projected or mutated.</param>
    /// <param name="template">
    /// The template the engine resolved for this work item (its frozen
    /// snapshot when it has one, otherwise the live type).
    /// </param>
    string? ResolveTaskStateId(WorkItem workItem, IWorkItemTemplate template);
}
