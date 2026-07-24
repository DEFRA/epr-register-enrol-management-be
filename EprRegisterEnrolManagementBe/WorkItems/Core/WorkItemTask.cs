using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// A unit of work that must be completed against a work item while it is in a
/// particular state. The framework only describes the contract — the engine that
/// drives task completion is delivered by RA-92.
///
/// Embedded verbatim in a frozen <see cref="WorkItemTemplateSnapshot"/>, so it
/// ignores extra BSON elements: a snapshot persisted under an older template
/// that carried since-removed fields must still deserialise rather than
/// throwing a <see cref="System.FormatException"/> for the whole worklist.
/// </summary>
[BsonIgnoreExtraElements]
public sealed record WorkItemTask(string Id, string DisplayName);