using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-000: builds the audit trail and <c>payload.currentQuery</c> that
/// <c>ReAccreditationFreshQuery</c> accepts as "stamped by this query
/// operation", for hook tests that construct a work item by hand: the stamp
/// and the query transition's <c>action-applied</c> entry carry the same
/// <see cref="QueryId"/>.
/// </summary>
internal static class FreshQueryTestData
{
    public static readonly DateTime SubmittedAt = new(2025, 11, 1, 9, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime EnteredFromStateAt = new(2025, 11, 2, 9, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime RaisedAt = new(2025, 11, 3, 10, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime TransitionAt = RaisedAt.AddMilliseconds(5);

    /// <summary>The id the stamp and the query transition share.</summary>
    public const string QueryId = "5b0c9f1e-2d4a-4c8e-9a71-0f3e6d2b8c41";

    /// <summary>Another query's id: an earlier query's stamp, or a racing request's.</summary>
    public const string StaleQueryId = "a7e4d3c2-1b0a-4f9e-8d7c-6b5a49382716";

    public const string Reason = "Tonnage does not reconcile";
    public static readonly string[] Sections = ["business-plan", "prn-tonnage"];

    private static readonly Dictionary<string, string> s_fromStateByAction = new()
    {
        ["query-during-duly-making"] = "submitted",
        ["query-during-duly-made"] = "duly-made",
        ["query-during-assessment"] = "assessment-in-progress",
        ["query-during-decision"] = "awaiting-decision",
    };

    public static string FromStateFor(string actionId) => s_fromStateByAction[actionId];

    public static BsonDocument CurrentQuery(DateTime? raisedAt = null, string queryId = QueryId) =>
        new()
        {
            ["queryId"] = queryId,
            ["reason"] = Reason,
            ["sections"] = new BsonArray(Sections),
            ["raisedAt"] = raisedAt ?? RaisedAt,
            ["raisedBy"] = "alice-1",
        };

    public static WorkItemAuditEntry ActionApplied(
        string actionId, string fromStateId, string toStateId, DateTime at, string? queryId = null)
    {
        var entry = new WorkItemAuditEntry
        {
            Action = "action-applied",
            ActionDisplayName = "Action applied",
            CreatedAt = at,
            Details = new()
            {
                ["actionId"] = actionId,
                ["fromStateId"] = fromStateId,
                ["toStateId"] = toStateId,
            },
        };
        if (queryId is not null)
        {
            entry.Details["queryId"] = queryId;
        }

        return entry;
    }

    /// <summary>
    /// Appends the entry that moved the item into <paramref name="fromStateId"/>
    /// (unless it is the initial state) and this query transition's own entry,
    /// as the engine leaves them on the instance it hands the hooks after the
    /// query service applied it for <paramref name="queryId"/>.
    /// </summary>
    public static void AppendQueryTransition(
        WorkItem workItem, string actionId, string fromStateId, string? queryId = QueryId)
    {
        if (fromStateId != "submitted")
        {
            workItem.AuditLog.Add(ActionApplied("enter-" + fromStateId, "submitted", fromStateId, EnteredFromStateAt));
        }

        workItem.AuditLog.Add(ActionApplied(actionId, fromStateId, "queried", TransitionAt, queryId));
    }
}
