using System.Text.Json;

namespace Backend.Api.WorkItems.Core;

/// <summary>
/// API representation of a persisted work item. Mirrors <see cref="WorkItem"/>
/// but carries the payload as a JSON element so callers do not see BSON types,
/// and projects engine state (current-state task progress and the actions the
/// engine will currently allow) so a UI can render without re-deriving it.
/// </summary>
public sealed record WorkItemResponse(
    Guid Id,
    string TypeId,
    string StateId,
    DateTime SubmittedAt,
    DateTime LastModifiedAt,
    string? SubmittedBy,
    JsonElement Payload,
    IReadOnlyCollection<WorkItemTaskProgress> Tasks,
    IReadOnlyCollection<WorkItemTransition> AvailableActions);
