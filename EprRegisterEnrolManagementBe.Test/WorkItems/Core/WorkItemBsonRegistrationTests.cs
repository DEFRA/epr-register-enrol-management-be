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

    // epr-81c: a freshly-constructed WorkItem (never round-tripped through
    // Mongo) must already use OrdinalIgnoreCase for both the outer
    // dictionary and the HashSet bucket. Otherwise behaviour diverges
    // depending solely on whether the document was just submitted or just
    // loaded from Mongo.
    [Fact]
    public void Freshly_constructed_dictionary_uses_case_insensitive_state_id_lookup()
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

        Assert.True(workItem.CompletedTaskIdsByState.ContainsKey("submitted"));
        Assert.True(workItem.CompletedTaskIdsByState.TryGetValue("SUBMITTED", out var bucket));
        Assert.Contains("task1", bucket!);
    }

    [Fact]
    public void Freshly_constructed_seeder_bucket_uses_case_insensitive_task_id_lookup()
    {
        // Mirrors how ReAccreditationSeeder builds buckets from a caller-
        // supplied collection — the resulting HashSet must honour the
        // OrdinalIgnoreCase contract on .Contains().
        var workItem = new WorkItem { TypeId = "test-type", StateId = "submitted" };
        workItem.CompletedTaskIdsByState["submitted"] =
            new HashSet<string>(new[] { "Task1" }, StringComparer.OrdinalIgnoreCase);

        var bucket = workItem.CompletedTaskIdsByState["submitted"];
        Assert.Contains("task1", bucket);
        Assert.Contains("TASK1", bucket);
    }

    // ---------------------- TaskStatusesByState (epr-gl6) ----------------------

    [Fact]
    public void Task_status_dictionary_round_trips_with_case_insensitive_lookups()
    {
        var workItem = new WorkItem
        {
            TypeId = "test-type",
            StateId = "Submitted",
            TaskStatusesByState =
            {
                ["Submitted"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["Task1"] = WorkItemTaskStatus.InProgress
                }
            }
        };

        var roundTripped = BsonSerializer.Deserialize<WorkItem>(workItem.ToBsonDocument());

        Assert.True(roundTripped.TaskStatusesByState.TryGetValue("submitted", out var inner));
        Assert.True(inner!.TryGetValue("task1", out var status));
        Assert.Equal(WorkItemTaskStatus.InProgress, status);
    }

    [Fact]
    public void Task_status_unknown_value_falls_back_to_not_started_for_forward_compat()
    {
        // Build a BSON document with an unknown status string, simulating a
        // future enum value being read back by an older binary. The reader
        // must tolerate it and fall back to NotStarted instead of throwing.
        var bson = new BsonDocument
        {
            ["_id"] = Guid.NewGuid().ToString(),
            ["TypeId"] = "test-type",
            ["StateId"] = "submitted",
            ["TaskStatusesByState"] = new BsonDocument
            {
                ["submitted"] = new BsonDocument { ["task1"] = "FromTheFuture" }
            }
        };

        var roundTripped = BsonSerializer.Deserialize<WorkItem>(bson);

        Assert.Equal(
            WorkItemTaskStatus.NotStarted,
            roundTripped.TaskStatusesByState["submitted"]["task1"]);
    }
}
