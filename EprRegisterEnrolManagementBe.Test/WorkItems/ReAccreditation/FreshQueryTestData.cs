using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-000: builds the audit trail and <c>payload.currentQuery</c> that
/// <c>ReAccreditationFreshQuery</c> accepts as "stamped by this query
/// operation", for hook tests that construct a work item by hand. The
/// timeline is: submitted, entered the from-state, query stamped, query
/// transition applied.
/// </summary>
internal static class FreshQueryTestData
{
    public static readonly DateTime SubmittedAt = new(2025, 11, 1, 9, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime EnteredFromStateAt = new(2025, 11, 2, 9, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime RaisedAt = new(2025, 11, 3, 10, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime TransitionAt = RaisedAt.AddMilliseconds(5);

    /// <summary>A date before <see cref="EnteredFromStateAt"/>: an earlier query's stamp.</summary>
    public static readonly DateTime StaleRaisedAt = new(2025, 10, 20, 10, 0, 0, DateTimeKind.Utc);

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

    public static BsonDocument CurrentQuery(DateTime? raisedAt = null) =>
        new()
        {
            ["reason"] = Reason,
            ["sections"] = new BsonArray(Sections),
            ["raisedAt"] = raisedAt ?? RaisedAt,
            ["raisedBy"] = "alice-1",
        };

    public static WorkItemAuditEntry ActionApplied(string actionId, string fromStateId, string toStateId, DateTime at) =>
        new()
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

    /// <summary>
    /// Appends the entry that moved the item into <paramref name="fromStateId"/>
    /// (unless it is the initial state) and this query transition's own entry,
    /// as the engine leaves them on the instance it hands the hooks.
    /// </summary>
    public static void AppendQueryTransition(WorkItem workItem, string actionId, string fromStateId)
    {
        if (fromStateId != "submitted")
        {
            workItem.AuditLog.Add(ActionApplied("enter-" + fromStateId, "submitted", fromStateId, EnteredFromStateAt));
        }

        workItem.AuditLog.Add(ActionApplied(actionId, fromStateId, "queried", TransitionAt));
    }
}
