using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

namespace Backend.Api.WorkItems.Core;

/// <summary>
/// A persisted work item. The framework owns the envelope (id, type, state,
/// timestamps, submitted-by, payload); modules describe what their payload
/// means via their <see cref="IWorkItemType"/> and operate on it via their
/// own service objects.
/// </summary>
public sealed class WorkItem
{
    [BsonId(IdGenerator = typeof(GuidGenerator))]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The <see cref="IWorkItemType.TypeId"/> this item is an instance of.</summary>
    public required string TypeId { get; init; }

    /// <summary>Current <see cref="WorkItemState.Id"/>. Set to the type's initial state on creation.</summary>
    public required string StateId { get; set; }

    /// <summary>UTC timestamp the work item was first accepted into the system.</summary>
    public DateTime SubmittedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Identifier of the upstream caller that submitted the item (CDP Cognito client id).</summary>
    public string? SubmittedBy { get; init; }

    /// <summary>
    /// Free-form, type-specific payload supplied by the upstream caller. Stored
    /// verbatim so modules can interpret it however they choose. Persisted as a
    /// BSON sub-document; the API converts to/from JSON at the boundary.
    /// </summary>
    public BsonDocument Payload { get; init; } = new();
}
