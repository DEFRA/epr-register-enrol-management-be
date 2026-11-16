using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-000: changes a module service needs committed together with a
/// transition. <see cref="IWorkItemService.ApplyActionAsync(Guid, string, WorkItemActionWrite, System.Security.Claims.ClaimsPrincipal, CancellationToken)"/>
/// applies them to the same loaded document as the state change and saves
/// everything in that transition's one versioned
/// <see cref="IWorkItemPersistence.ReplaceAsync"/>. Either all of it is
/// written or none of it is. A request that loses a race to another writer
/// gets <see cref="WorkItemActionFailureCode.ConcurrencyConflict"/> (or, once
/// the winner has moved the item on, <see cref="WorkItemActionFailureCode.InvalidTransition"/>)
/// and leaves nothing behind.
///
/// Use this instead of a separate write before the transition. A separate
/// write is not tied to the item's version or state, so an overlapping
/// request can overwrite it, and a failed transition leaves it behind.
/// </summary>
public sealed record WorkItemActionWrite
{
    /// <summary>
    /// Assign the item as part of the transition. Audited as <c>assigned</c>,
    /// and fans out to <see cref="IWorkItemPostActionHook.OnAssignmentChangedAsync"/>,
    /// exactly as <see cref="IWorkItemService.AssignAsync"/> does. Assigning to
    /// the current assignee changes nothing and fires no assignment hook.
    /// </summary>
    public WorkItemAssignee? AssignTo { get; init; }

    /// <summary>
    /// Top-level payload fields to set (no dotted paths, no <c>$</c>
    /// operators). Each value replaces the whole field; every other payload
    /// field is saved exactly as loaded.
    /// </summary>
    public IReadOnlyDictionary<string, BsonValue> PayloadFields { get; init; } =
        new Dictionary<string, BsonValue>();

    /// <summary>
    /// Extra details on this transition's <c>action-applied</c> audit entry,
    /// so a post-action hook can tie what it reads back to this transition.
    /// May not reuse a key the engine writes itself
    /// (<see cref="WorkItemService.ActionAppliedDetailKeys"/>).
    /// </summary>
    public IReadOnlyDictionary<string, string?> ActionAppliedDetails { get; init; } =
        new Dictionary<string, string?>();
}

/// <summary>Who a <see cref="WorkItemActionWrite"/> assigns the item to.</summary>
public sealed record WorkItemAssignee(string Id, string? Name);
