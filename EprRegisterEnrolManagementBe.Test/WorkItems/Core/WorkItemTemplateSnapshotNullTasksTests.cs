using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// epr-dtkw: <see cref="WorkItemTemplateSnapshot.TasksByState"/> is declared
/// <c>required</c>, but <c>required</c> is a compile-time contract the BSON
/// deserialiser does not honour. A stored snapshot written without a
/// <c>tasksByState</c> element deserialises with the property NULL, and
/// <see cref="WorkItemTemplateSnapshot.GetTasksForState"/> then threw.
///
/// <para>
/// These tests go through <see cref="BsonSerializer"/> rather than constructing
/// the snapshot in C#, because C# will not LET you build the broken object —
/// <c>required</c> is enforced at the constructor. Only the deserialiser can
/// produce it, so only the deserialiser can reproduce the defect. An
/// object-initialiser test here would pass while production kept crashing.
/// </para>
/// </summary>
public class WorkItemTemplateSnapshotNullTasksTests
{
    /// <summary>
    /// The precondition the rest of this file depends on. If a future driver or
    /// convention starts materialising the dictionary, this test fails and the
    /// others become vacuous — which is exactly when someone should be told.
    /// </summary>
    [Fact]
    public void A_snapshot_stored_without_tasksByState_deserialises_with_it_null()
    {
        var snapshot = DeserialiseSnapshotWithoutTasks();

        Assert.Null(snapshot.TasksByState);
    }

    [Fact]
    public void GetTasksForState_returns_empty_when_tasksByState_is_absent()
    {
        var snapshot = DeserialiseSnapshotWithoutTasks();

        Assert.Empty(snapshot.GetTasksForState("submitted"));
    }

    /// <summary>
    /// The two production paths this took out. Projecting the work item builds
    /// the duly-making response AFTER the transition is committed, so the throw
    /// left the application duly made and the regulator looking at a 500; the
    /// snapshot migration aborted its whole batch on the first such document and
    /// re-failed on every boot.
    /// </summary>
    [Theory]
    [InlineData("submitted")]
    [InlineData("duly-made")]
    [InlineData("a-state-the-snapshot-never-heard-of")]
    public void GetTasksForState_does_not_throw_for_any_state(string stateId)
    {
        var snapshot = DeserialiseSnapshotWithoutTasks();

        var tasks = snapshot.GetTasksForState(stateId);

        Assert.Empty(tasks);
    }

    /// <summary>
    /// The guard must not swallow the normal case: a snapshot that DOES record
    /// tasks still returns them.
    /// </summary>
    [Fact]
    public void GetTasksForState_still_returns_recorded_tasks()
    {
        var document = SnapshotDocument();
        document["tasksByState"] = new BsonDocument
        {
            ["submitted"] = new BsonArray
            {
                new BsonDocument { ["id"] = "verify-organisation-details", ["displayName"] = "Verify" },
            },
        };

        var snapshot = BsonSerializer.Deserialize<WorkItemTemplateSnapshot>(document);

        Assert.Single(snapshot.GetTasksForState("submitted"));
        Assert.Empty(snapshot.GetTasksForState("duly-made"));
    }

    private static WorkItemTemplateSnapshot DeserialiseSnapshotWithoutTasks() =>
        BsonSerializer.Deserialize<WorkItemTemplateSnapshot>(SnapshotDocument());

    /// <summary>
    /// A snapshot with no <c>tasksByState</c> element at all — the shape found
    /// on the documents that were failing.
    /// </summary>
    private static BsonDocument SnapshotDocument() =>
        new()
        {
            ["templateVersion"] = "v10",
            ["states"] = new BsonArray
            {
                new BsonDocument
                {
                    ["id"] = "submitted",
                    ["displayName"] = "Not started",
                    ["isTerminal"] = false,
                },
            },
            ["transitions"] = new BsonArray(),
        };
}
