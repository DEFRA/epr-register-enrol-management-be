using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// Regression tests for epr-aq5: <see cref="WorkItem.CompletedTaskIdsByState"/>
/// must round-trip through BSON with case-insensitive comparers on both
/// the dictionary keys and every <see cref="HashSet{T}"/> bucket, because
/// the engine compares state ids and task ids
/// <see cref="StringComparison.OrdinalIgnoreCase"/> elsewhere.
/// </summary>
public class WorkItemBsonRegistrationTests
{
    static WorkItemBsonRegistrationTests() => WorkItemBsonRegistration.Register();

    [Fact]
    public void Bucket_reloaded_from_bson_contains_task_id_with_different_casing()
    {
        var workItem = new WorkItem
        {
            TypeId = "test-type",
            StateId = "submitted",
            CompletedTaskIdsByState =
            {
                ["submitted"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Task1" }
            }
        };

        var roundTripped = BsonSerializer.Deserialize<WorkItem>(workItem.ToBsonDocument());

        var bucket = roundTripped.CompletedTaskIdsByState["submitted"];
        // Bucket comparer must be OrdinalIgnoreCase after a Mongo round-trip
        // (task ids are compared case-insensitively throughout the engine).
        Assert.Contains("task1", bucket);
        Assert.Contains("TASK1", bucket);
        Assert.Contains("Task1", bucket);
    }

    [Fact]
    public void Dictionary_reloaded_from_bson_resolves_state_id_with_different_casing()
    {
        var workItem = new WorkItem
        {
            TypeId = "test-type",
            StateId = "Submitted",
            CompletedTaskIdsByState =
            {
                ["Submitted"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "task1" }
            }
        };

        var roundTripped = BsonSerializer.Deserialize<WorkItem>(workItem.ToBsonDocument());

        Assert.True(roundTripped.CompletedTaskIdsByState.ContainsKey("submitted"),
            "After a Mongo round-trip the dictionary must still match state ids case-insensitively.");
        Assert.True(roundTripped.CompletedTaskIdsByState.TryGetValue("SUBMITTED", out var bucket));
        Assert.Contains("task1", bucket!);
    }

    [Fact]
    public void Bucket_reloaded_from_bson_with_default_dotnet_dictionary_still_normalises()
    {
        // Simulate the pre-fix wire shape: a plain Dictionary/HashSet with
        // the default (case-sensitive) comparer, written via the default
        // serializer and read back via our scoped serializer.
        var raw = new Dictionary<string, HashSet<string>>
        {
            ["submitted"] = new() { "Task1" }
        };
        var workItem = new WorkItem
        {
            TypeId = "test-type",
            StateId = "submitted",
            CompletedTaskIdsByState = raw
        };

        var bson = workItem.ToBsonDocument();
        var roundTripped = BsonSerializer.Deserialize<WorkItem>(bson);

        Assert.Contains("task1", roundTripped.CompletedTaskIdsByState["submitted"]);
    }

    [Fact]
    public void Empty_dictionary_round_trips_without_error()
    {
        var workItem = new WorkItem { TypeId = "test-type", StateId = "submitted" };

        var roundTripped = BsonSerializer.Deserialize<WorkItem>(workItem.ToBsonDocument());

        Assert.Empty(roundTripped.CompletedTaskIdsByState);
    }
}
