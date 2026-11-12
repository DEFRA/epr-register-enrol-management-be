using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-000: a post-action hook only acts on a <c>currentQuery</c> stamped for
/// the query transition it is handling.
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
        bool nullSections = false) =>
        new()
        {
            Reason = reason,
            Sections = nullSections ? null : sections ?? FreshQueryTestData.Sections,
            RaisedAt = nullRaisedAt ? null : raisedAt ?? FreshQueryTestData.RaisedAt,
            RaisedBy = "alice-1",
        };

    private static WorkItem QueriedItem(string actionId = ActionId, string fromStateId = FromStateId)
    {
        var item = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "queried",
            SubmittedAt = FreshQueryTestData.SubmittedAt,
        };
        FreshQueryTestData.AppendQueryTransition(item, actionId, fromStateId);
        return item;
    }

    [Fact]
    public void A_query_stamped_after_the_item_entered_its_state_and_before_the_transition_is_fresh()
    {
        var query = Query();

        var result = ReAccreditationFreshQuery.Verify(QueriedItem(), query, ActionId, FromStateId);

        Assert.True(result.IsFresh);
        Assert.Same(query, result.Query);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void An_empty_reason_is_fresh_because_RA_534_made_the_reason_optional()
    {
        var result = ReAccreditationFreshQuery.Verify(QueriedItem(), Query(reason: string.Empty), ActionId, FromStateId);

        Assert.True(result.IsFresh);
    }

    [Fact]
    public void A_stamp_in_the_same_instant_as_the_transition_into_the_from_state_is_fresh()
    {
        var result = ReAccreditationFreshQuery.Verify(
            QueriedItem(), Query(raisedAt: FreshQueryTestData.EnteredFromStateAt), ActionId, FromStateId);

        Assert.True(result.IsFresh);
    }

    [Fact]
    public void With_no_earlier_transition_the_lower_bound_is_the_submission()
    {
        var item = QueriedItem("query-during-duly-making", "submitted");

        Assert.True(ReAccreditationFreshQuery.Verify(
            item, Query(), "query-during-duly-making", "submitted").IsFresh);

        var stale = ReAccreditationFreshQuery.Verify(
            item, Query(raisedAt: FreshQueryTestData.SubmittedAt.AddDays(-1)), "query-during-duly-making", "submitted");
        Assert.False(stale.IsFresh);
        Assert.Contains("belongs to an earlier query", stale.FailureReason);
    }

    [Theory]
    [InlineData("missing", "payload.currentQuery is missing")]
    [InlineData("raisedAt", "payload.currentQuery.raisedAt is missing")]
    [InlineData("sections-null", "payload.currentQuery.sections is missing or empty")]
    [InlineData("sections-empty", "payload.currentQuery.sections is missing or empty")]
    [InlineData("reason", "payload.currentQuery.reason is missing")]
    public void A_missing_value_is_not_fresh(string missing, string expectedReason)
    {
        var query = missing switch
        {
            "missing" => null,
            "raisedAt" => Query(nullRaisedAt: true),
            "sections-null" => Query(nullSections: true),
            "sections-empty" => Query(sections: []),
            "reason" => Query(reason: null),
            _ => throw new ArgumentOutOfRangeException(nameof(missing)),
        };

        var result = ReAccreditationFreshQuery.Verify(QueriedItem(), query, ActionId, FromStateId);

        Assert.False(result.IsFresh);
        Assert.Null(result.Query);
        Assert.Equal(expectedReason, result.FailureReason);
    }

    [Fact]
    public void A_query_raised_before_the_item_entered_its_from_state_is_stale()
    {
        var result = ReAccreditationFreshQuery.Verify(
            QueriedItem(), Query(raisedAt: FreshQueryTestData.StaleRaisedAt), ActionId, FromStateId);

        Assert.False(result.IsFresh);
        Assert.StartsWith("payload.currentQuery was raised at 2025-10-20T10:00:00", result.FailureReason);
        Assert.Contains($"before the work item entered '{FromStateId}' at 2025-11-02T09:00:00", result.FailureReason);
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
        item.AuditLog.Add(FreshQueryTestData.ActionApplied(ActionId, FromStateId, "queried", t0.AddDays(2)));
        item.AuditLog.Add(new WorkItemAuditEntry
        {
            Action = "application-queried",
            ActionDisplayName = "Application queried",
            CreatedAt = t0.AddDays(2),
        });
        item.AuditLog.Add(FreshQueryTestData.ActionApplied("resume-during-duly-made", "queried", "updated", t0.AddDays(3)));
        item.AuditLog.Add(FreshQueryTestData.ActionApplied("continue-review-during-duly-made", "updated", FromStateId, t0.AddDays(4)));
        item.AuditLog.Add(FreshQueryTestData.ActionApplied(ActionId, FromStateId, "queried", t0.AddDays(5)));

        var firstQueryStamp = Query(raisedAt: t0.AddDays(2).AddSeconds(-1));
        var result = ReAccreditationFreshQuery.Verify(item, firstQueryStamp, ActionId, FromStateId);

        Assert.False(result.IsFresh);
        Assert.Contains("belongs to an earlier query", result.FailureReason);
    }

    [Fact]
    public void A_query_raised_after_the_transition_is_not_fresh()
    {
        var result = ReAccreditationFreshQuery.Verify(
            QueriedItem(), Query(raisedAt: FreshQueryTestData.TransitionAt.AddSeconds(1)), ActionId, FromStateId);

        Assert.False(result.IsFresh);
        Assert.Contains($"after the {ActionId} transition", result.FailureReason);
    }

    [Fact]
    public void An_item_with_no_action_applied_entry_is_not_fresh()
    {
        var item = new WorkItem { TypeId = ReAccreditationType.Id, StateId = "queried" };

        var result = ReAccreditationFreshQuery.Verify(item, Query(), ActionId, FromStateId);

        Assert.False(result.IsFresh);
        Assert.Equal(
            $"the audit log has no action-applied entry for {ActionId} from '{FromStateId}' to 'queried'",
            result.FailureReason);
    }

    [Theory]
    [InlineData("query-during-decision", FromStateId)]
    [InlineData(ActionId, "awaiting-decision")]
    public void A_latest_action_applied_entry_for_a_different_transition_is_not_fresh(
        string handledActionId,
        string handledFromStateId)
    {
        var result = ReAccreditationFreshQuery.Verify(QueriedItem(), Query(), handledActionId, handledFromStateId);

        Assert.False(result.IsFresh);
        Assert.StartsWith("the audit log has no action-applied entry", result.FailureReason);
    }
}
