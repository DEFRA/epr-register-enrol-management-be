namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Module-supplied seam that lets a work item type say "this item is sitting
/// in a waypoint, and the state it will return to when that waypoint
/// discharges is <em>this</em> one".
///
/// The engine normally treats a work item's <see cref="WorkItem.StateId"/> as
/// the whole answer to "where is this item in its lifecycle". For most types
/// it is. It comes apart for a waypoint state — a state an item passes
/// through on its way back to somewhere else, where the question a caller
/// actually needs answered is "back to where?". The re-accreditation module's
/// <c>updated</c> state (RA-337) is the motivating case: an application
/// queried mid-review lands there once the operator responds, and the case
/// management frontend has to decide which call to action to offer, which
/// depends entirely on the state the query was raised from.
///
/// RA-410: this seam was previously <c>IWorkItemTaskStateResolver</c> and
/// answered "whose checklist applies while the item is here". The checklists
/// are gone along with the rest of the task framework, but the waypoint-origin
/// question outlived them: it is derived from a work item's own audit history,
/// so no client can answer it locally. What remains is a pure read projection
/// — it feeds <see cref="WorkItemResponse.OriginStateId"/> and nothing else,
/// and no longer gates any transition.
///
/// Keeping this a resolver rather than a rule in the engine is the point.
/// Core must not know that a state called <c>updated</c> exists, or that
/// <c>resume-during-*</c> actions mean anything — other types have no such
/// concept. Core only knows that a type may have an opinion about where an
/// item is headed back to, and asks.
///
/// Implementations must:
/// <list type="bullet">
///   <item>Declare the <see cref="TypeId"/> they belong to. The engine only
///   consults a resolver for work items of that type, so resolvers from
///   different modules can never compete and the outcome never depends on DI
///   registration order.</item>
///   <item>Return <c>null</c> when they have no opinion. The engine falls back
///   to <see cref="WorkItem.StateId"/>, so a resolver that abstains is
///   invisible.</item>
///   <item>Be pure and side-effect free. This runs on every read projection,
///   so it must not perform I/O.</item>
///   <item>Resolve against the supplied template — the work item's own frozen
///   snapshot — rather than the live type, so an in-flight item is judged by
///   the rules it was submitted under.</item>
/// </list>
/// </summary>
public interface IWorkItemOriginStateResolver
{
    /// <summary>
    /// The <see cref="WorkItem.TypeId"/> this resolver speaks for. The engine
    /// skips it entirely for work items of any other type, so an
    /// implementation never has to guard on type itself.
    /// </summary>
    string TypeId { get; }

    /// <summary>
    /// The id of the state <paramref name="workItem"/> will return to when its
    /// current waypoint discharges, or <c>null</c> to defer to
    /// <see cref="WorkItem.StateId"/>.
    /// </summary>
    /// <param name="workItem">The work item being projected.</param>
    /// <param name="template">
    /// The template the engine resolved for this work item (its frozen
    /// snapshot when it has one, otherwise the live type).
    /// </param>
    string? ResolveOriginStateId(WorkItem workItem, IWorkItemTemplate template);
}
