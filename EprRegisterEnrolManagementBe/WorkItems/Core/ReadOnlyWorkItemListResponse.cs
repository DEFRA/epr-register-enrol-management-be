using EprRegisterEnrolManagementBe.Utils.Logging;
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
    internal static ReadOnlyWorkItemListItem From(WorkItem item, IStructuredLogger<WorkItemEndpointsLogger> log) =>
        new(
            item.Id,
            item.TypeId,
            item.StateId,
            item.LastModifiedAt,
            item.AssignedToId,
            item.AssignedToName,
            new ReadOnlyWorkItemPayload(
                OptionalString(item, "applicationReference", log),
                OptionalString(item, "operatorOrganisationId", log),
                OptionalString(item, "organisationName", log)
            )
        );

    // Absent or BSON null → null: these fields are optional on stored items
    // (applicationReference predates RA-219 on old items).
    //
    // A value of any other BSON type is corrupt data. It also comes back as
    // null, logged at Error with the work item id and the field name (never
    // the value, which may be personal data), so one bad record cannot turn
    // the whole results page into a 500. The Admin UI (RA-751) shows a null
    // field as "Missing from work item" and logs it on its side too.
    private static string? OptionalString(
        WorkItem item,
        string field,
        IStructuredLogger<WorkItemEndpointsLogger> log
    )
    {
        if (!item.Payload.TryGetValue(field, out var value) || value.IsBsonNull)
        {
            return null;
        }
        if (value.IsString)
        {
            return value.AsString;
        }
        log.Log(
            LogLevel.Error,
            "Work item payload field is not a string; returned as null to the read-only caller",
            new Dictionary<string, object?>
            {
                ["work_item.id"] = item.Id.ToString(),
                ["work_item.payload_field"] = field,
                ["work_item.payload_field_bson_type"] = value.BsonType.ToString(),
            }
        );
        return null;
    }
}

/// <summary>The only payload fields a read-only caller receives.</summary>
public sealed record ReadOnlyWorkItemPayload(
    string? ApplicationReference,
    string? OperatorOrganisationId,
    string? OrganisationName
);
