using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-000: a post-action hook only acts on a <c>currentQuery</c> stamped for
/// the query transition it is handling: its query id must match the one on
/// that transition's <c>action-applied</c> entry. No timestamp is compared.
/// </summary>
public class ReAccreditationFreshQueryTests
{
    private const string ActionId = "query-during-duly-made";
    private const string FromStateId = "duly-made";

    private static CurrentQuery Query(
        DateTime? raisedAt = null,
        IReadOnlyList<string>? sections = null,
        string? reason = FreshQueryTestData.Reason,
        bool nullRaisedAt = false,
        bool nullSections = false,
        string? queryId = FreshQueryTestData.QueryId) =>
        new()
        {
            QueryId = queryId,
            Reason = reason,
            Sections = nullSections ? null : sections ?? FreshQueryTestData.Sections,
            RaisedAt = nullRaisedAt ? null : raisedAt ?? FreshQueryTestData.RaisedAt,
            RaisedBy = "alice-1",
        };

    private static WorkItem QueriedItem(
        string actionId = ActionId,
        string fromStateId = FromStateId,
        string? transitionQueryId = FreshQueryTestData.QueryId)
    {
        var item = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "queried",
            SubmittedAt = FreshQueryTestData.SubmittedAt,
        };
        FreshQueryTestData.AppendQueryTransition(item, actionId, fromStateId, transitionQueryId);
        return item;
    }

    private static (bool Fresh, ReAccreditationFreshQuery.VerifiedQuery? Query, string? Reason) Verify(
        WorkItem workItem,
        CurrentQuery? currentQuery,
        string actionId,
        string fromStateId)
    {
        var fresh = ReAccreditationFreshQuery.TryVerify(
            workItem, currentQuery, actionId, fromStateId, out var query, out var reason);
        return (fresh, query, reason);
    }

    [Fact]
    public void A_query_whose_id_matches_the_transition_is_fresh()
    {
        var result = Verify(QueriedItem(), Query(), ActionId, FromStateId);

        Assert.True(result.Fresh);
        Assert.Equal(FreshQueryTestData.Reason, result.Query!.Reason);
        Assert.Equal(FreshQueryTestData.Sections, result.Query.Sections);
        Assert.Equal(FreshQueryTestData.RaisedAt, result.Query.RaisedAt);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void An_empty_reason_is_fresh_because_RA_534_made_the_reason_optional()
    {
        var result = Verify(QueriedItem(), Query(reason: string.Empty), ActionId, FromStateId);

        Assert.True(result.Fresh);
    }

    [Theory]
    // The pod that moved the item into duly-made ran ahead: the stamp looks
    // older than the item's arrival in its from-state.
    [InlineData(-1)]
    // The stamping pod ran ahead of the one whose clock the transition used.
    [InlineData(1)]
    public void Clock_skew_between_pods_does_not_fail_a_genuine_query(int direction)
    {
        var raisedAt = direction < 0
            ? FreshQueryTestData.EnteredFromStateAt.AddMinutes(-5)
            : FreshQueryTestData.TransitionAt.AddMinutes(5);

        var result = Verify(QueriedItem(), Query(raisedAt: raisedAt), ActionId, FromStateId);

        Assert.True(result.Fresh, result.Reason);
        Assert.Equal(raisedAt, result.Query!.RaisedAt);
    }

    [Theory]
    [InlineData("missing", "payload.currentQuery is missing")]
    [InlineData("raisedAt", "payload.currentQuery.raisedAt is missing")]
    [InlineData("sections-null", "payload.currentQuery.sections is missing or empty")]
    [InlineData("sections-empty", "payload.currentQuery.sections is missing or empty")]
    [InlineData("reason", "payload.currentQuery.reason is missing")]
    [InlineData("queryId", "payload.currentQuery.queryId is missing, so it belongs to an earlier query")]
    public void A_missing_value_is_not_fresh(string missing, string expectedReason)
    {
        var query = missing switch
        {
            "missing" => null,
            "raisedAt" => Query(nullRaisedAt: true),
            "sections-null" => Query(nullSections: true),
            "sections-empty" => Query(sections: []),
            "reason" => Query(reason: null),
            // A stamp written before RA-000 has no query id.
            "queryId" => Query(queryId: null),
            _ => throw new ArgumentOutOfRangeException(nameof(missing)),
        };

        var result = Verify(QueriedItem(), query, ActionId, FromStateId);

        Assert.False(result.Fresh);
        Assert.Null(result.Query);
        Assert.Equal(expectedReason, result.Reason);
    }

    [Fact]
    public void A_query_with_another_queries_id_is_not_fresh()
    {
        // An earlier query's stamp, or one written by a request that did not
        // apply this transition.
        var result = Verify(
            QueriedItem(), Query(queryId: FreshQueryTestData.StaleQueryId), ActionId, FromStateId);

        Assert.False(result.Fresh);
        Assert.Equal(
            $"payload.currentQuery is query {FreshQueryTestData.StaleQueryId}, but the {ActionId} transition "
                + $"was applied for query {FreshQueryTestData.QueryId}",
            result.Reason);
    }

    [Fact]
    public void The_previous_query_is_stale_after_the_item_resumed_and_returned_to_the_from_state()
    {
        // Real history shape: queried out of duly-made, resumed, reviewed back
        // into duly-made, queried again without a new stamp.
        var item = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "queried",
            SubmittedAt = FreshQueryTestData.SubmittedAt,
        };
        var t0 = FreshQueryTestData.SubmittedAt;
        item.AuditLog.Add(FreshQueryTestData.ActionApplied("duly-make", "submitted", "duly-made", t0.AddDays(1)));
        item.AuditLog.Add(FreshQueryTestData.ActionApplied(
            ActionId, FromStateId, "queried", t0.AddDays(2), FreshQueryTestData.StaleQueryId));
        item.AuditLog.Add(new WorkItemAuditEntry
        {
            Action = "application-queried",
            ActionDisplayName = "Application queried",
            CreatedAt = t0.AddDays(2),
        });
        item.AuditLog.Add(FreshQueryTestData.ActionApplied("resume-during-duly-made", "queried", "updated", t0.AddDays(3)));
        item.AuditLog.Add(FreshQueryTestData.ActionApplied("continue-review-during-duly-made", "updated", FromStateId, t0.AddDays(4)));
        item.AuditLog.Add(FreshQueryTestData.ActionApplied(
            ActionId, FromStateId, "queried", t0.AddDays(5), FreshQueryTestData.QueryId));

        var firstQueryStamp = Query(queryId: FreshQueryTestData.StaleQueryId);
        var result = Verify(item, firstQueryStamp, ActionId, FromStateId);

        Assert.False(result.Fresh);
        Assert.StartsWith($"payload.currentQuery is query {FreshQueryTestData.StaleQueryId}", result.Reason);
    }

    [Fact]
    public void A_transition_not_applied_by_the_query_service_is_not_fresh()
    {
        // No queryId on the action-applied entry: the transition was applied
        // without ReAccreditationQueryService, so no stamp belongs to it.
        var result = Verify(QueriedItem(transitionQueryId: null), Query(), ActionId, FromStateId);

        Assert.False(result.Fresh);
        Assert.Equal(
            $"the {ActionId} transition's action-applied entry records no queryId, "
                + "so it was not applied by the query service",
            result.Reason);
    }

    [Fact]
    public void An_item_with_no_action_applied_entry_is_not_fresh()
    {
        var item = new WorkItem { TypeId = ReAccreditationType.Id, StateId = "queried" };

        var result = Verify(item, Query(), ActionId, FromStateId);

        Assert.False(result.Fresh);
        Assert.Equal(
            $"the audit log has no action-applied entry for {ActionId} from '{FromStateId}' to 'queried'",
            result.Reason);
    }

    [Theory]
    [InlineData("query-during-decision", FromStateId)]
    [InlineData(ActionId, "awaiting-decision")]
    public void A_latest_action_applied_entry_for_a_different_transition_is_not_fresh(
        string handledActionId,
        string handledFromStateId)
    {
        var result = Verify(QueriedItem(), Query(), handledActionId, handledFromStateId);

        Assert.False(result.Fresh);
        Assert.StartsWith("the audit log has no action-applied entry", result.Reason);
    }
}
