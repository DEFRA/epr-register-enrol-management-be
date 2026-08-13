using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-364: the work item projection now filters on
/// <see cref="WorkItemTransition.CallerInvocable"/>, so what that flag
/// deserialises to for a template snapshot frozen *before* the flag existed
/// (it was added in RA-337) stopped being cosmetic and became load-bearing.
///
/// These tests pin the actual driver behaviour rather than the behaviour we
/// might wish for. The recorded behaviour is a hazard, documented here so the
/// next person to touch this does not rediscover it the hard way:
///
/// <list type="bullet">
/// <item>A transition persisted without a <c>callerInvocable</c> element
/// deserialises to <c>false</c>, not to the record's <c>true</c> default —
/// the driver uses <c>default(bool)</c> for a missing element and does not
/// honour the primary-constructor default.</item>
/// <item>Consequently a legacy snapshot's legitimate transitions (withdraw,
/// approve, query-during-*) read back as non-caller-invocable.</item>
/// </list>
///
/// This is a pre-existing data-shape defect, not one RA-364 introduces: the
/// generic action endpoint's own <c>CallerInvocable: false</c> gate
/// (WorkItemEndpoints.Action) reads the same snapshot and has always rejected
/// those transitions. RA-364 only makes the projection agree with that gate,
/// so the UI stops advertising actions the endpoint would reject. Repairing
/// the persisted values is a separate data migration — tracked as follow-up
/// work, deliberately out of scope here.
/// </summary>
public class WorkItemTransitionLegacySnapshotTests
{
    [Fact]
    public void Transition_persisted_without_caller_invocable_element_reads_back_as_false()
    {
        // A transition exactly as a pre-RA-337 snapshot persisted it: every
        // element present except CallerInvocable, which did not exist yet.
        var legacy = new BsonDocument
        {
            { "actionId", "withdraw-during-query" },
            { "displayName", "Withdraw" },
            { "fromStateId", "queried" },
            { "toStateId", "withdrawn" },
            { "requiresAllTasksComplete", false },
        };

        var transition = BsonSerializer.Deserialize<WorkItemTransition>(legacy);

        Assert.Equal("withdraw-during-query", transition.ActionId);

        // The hazard: NOT the record's `CallerInvocable = true` default. A
        // missing element becomes default(bool). Asserted so that if a driver
        // upgrade or a [BsonDefaultValue] ever changes this, the change is
        // surfaced deliberately rather than silently altering which actions
        // legacy in-flight cases are offered.
        Assert.False(transition.CallerInvocable);
    }

    [Fact]
    public void Transition_with_explicit_caller_invocable_true_round_trips()
    {
        var transition = new WorkItemTransition(
            "payment-received",
            "Payment received",
            "duly-made",
            "assessment-in-progress"
        );

        var reloaded = BsonSerializer.Deserialize<WorkItemTransition>(transition.ToBsonDocument());

        Assert.True(reloaded.CallerInvocable);
        Assert.Equal(transition, reloaded);
    }

    [Fact]
    public void Transition_with_explicit_caller_invocable_false_round_trips()
    {
        var transition = new WorkItemTransition(
            "resume-during-assessment",
            "Resume",
            "queried",
            "updated",
            CallerInvocable: false
        );

        var reloaded = BsonSerializer.Deserialize<WorkItemTransition>(transition.ToBsonDocument());

        Assert.False(reloaded.CallerInvocable);
        Assert.Equal(transition, reloaded);
    }
}
