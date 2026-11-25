using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-751 / ADR-0010: the <c>GET /work-items</c> page a read-only caller (the
/// Admin UI) receives. It carries only what the Admin UI's work item search
/// shows, so contact details and the rest of the payload never leave this
/// service for that caller, whatever the caller's own code does. Other callers
/// get <see cref="WorkItemListResponse"/>, unchanged.
/// </summary>
public sealed record ReadOnlyWorkItemListResponse(
    IReadOnlyList<ReadOnlyWorkItemListItem> Items,
    long TotalCount,
    int Page,
    int PageSize
);

/// <summary>One work item as a read-only caller sees it.</summary>
public sealed record ReadOnlyWorkItemListItem(
    Guid Id,
    string TypeId,
    string StateId,
    DateTime LastModifiedAt,
    string? AssignedToId,
    string? AssignedToName,
    ReadOnlyWorkItemPayload Payload
)
{
    internal static ReadOnlyWorkItemListItem From(WorkItem item) =>
        new(
            item.Id,
            item.TypeId,
            item.StateId,
            item.LastModifiedAt,
            item.AssignedToId,
            item.AssignedToName,
            new ReadOnlyWorkItemPayload(
                OptionalString(item.Payload, "applicationReference"),
                OptionalString(item.Payload, "operatorOrganisationId"),
                OptionalString(item.Payload, "organisationName")
            )
        );

    // Absent or BSON null → null (these fields are optional on stored items:
    // applicationReference predates RA-219 on old items). A value of any other
    // BSON type throws, so corrupt data surfaces as a 500 rather than a blank.
    private static string? OptionalString(BsonDocument payload, string field) =>
        payload.TryGetValue(field, out var value) && !value.IsBsonNull ? value.AsString : null;
}

/// <summary>The only payload fields a read-only caller receives.</summary>
public sealed record ReadOnlyWorkItemPayload(
    string? ApplicationReference,
    string? OperatorOrganisationId,
    string? OrganisationName
);
