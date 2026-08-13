using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Frozen copy of an <see cref="IWorkItemType"/>'s template (states,
/// transitions and version) captured when a work item is first submitted.
/// Stored alongside the work item so that — even if the live module's
/// templates evolve later — the work item and its audit history continue to
/// render with the same action set and template version they were assessed
/// against.
///
/// Snapshots are taken eagerly at submission rather than lazily on read so
/// that the frozen view survives the live module being unregistered or its
/// state machine changing.
///
/// By design a frozen snapshot can contain fields the live model has since
/// removed, so it ignores extra BSON elements: a snapshot persisted under an
/// older template must still deserialise when the worklist is queried rather
/// than throwing a <see cref="System.FormatException"/> for the whole batch.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class WorkItemTemplateSnapshot : IWorkItemTemplate
{
    public required string TemplateVersion { get; init; }

    public required IReadOnlyCollection<WorkItemState> States { get; init; }

    public required IReadOnlyCollection<WorkItemTransition> Transitions { get; init; }

    /// <summary>
    /// Build a snapshot from a live <see cref="IWorkItemType"/>, so the
    /// snapshot is self-contained and does not need to call the live type
    /// again later.
    /// </summary>
    public static WorkItemTemplateSnapshot Capture(IWorkItemType type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return new WorkItemTemplateSnapshot
        {
            TemplateVersion = type.TemplateVersion,
            States = type.States.ToList(),
            Transitions = type.Transitions.ToList()
        };
    }
}