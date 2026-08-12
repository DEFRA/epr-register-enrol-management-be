using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// A possible state of a work item. <see cref="IsTerminal"/> marks states from which
/// no further progress is expected (for example "approved" or "rejected").
///
/// Embedded verbatim in a frozen <see cref="WorkItemTemplateSnapshot"/>, so it
/// ignores extra BSON elements: a snapshot persisted under an older template
/// that carried since-removed fields must still deserialise rather than
/// throwing a <see cref="System.FormatException"/> for the whole worklist.
/// </summary>
[BsonIgnoreExtraElements]
public sealed record WorkItemState(string Id, string DisplayName, bool IsTerminal = false);