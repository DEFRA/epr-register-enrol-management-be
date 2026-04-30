using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// Direct unit coverage for <see cref="WorkItemPersistence.BuildFilter"/>.
/// The class as a whole sits behind a real Mongo driver, but the filter
/// construction is pure logic and is the highest-leverage thing to keep
/// inside the coverage gate (epr-036).
/// </summary>
public class WorkItemPersistenceBuildFilterTests
{
    private static readonly IBsonSerializer<WorkItem> s_workItemSerializer =
        BsonSerializer.SerializerRegistry.GetSerializer<WorkItem>();

    private static BsonDocument Render(WorkItemQuery query)
    {
        var filter = WorkItemPersistence.BuildFilter(query);
        return filter.Render(new RenderArgs<WorkItem>(s_workItemSerializer, BsonSerializer.SerializerRegistry));
    }

    [Fact]
    public void EmptyQueryRendersEmptyFilter()
    {
        var doc = Render(new WorkItemQuery());

        Assert.Equal(new BsonDocument(), doc);
    }

    [Fact]
    public void TypeIdsRenderAsInClause()
    {
        var doc = Render(new WorkItemQuery(TypeIds: new[] { "re-accreditation", "registration" }));

        var expected = new BsonDocument("TypeId", new BsonDocument("$in",
            new BsonArray { "re-accreditation", "registration" }));
        Assert.Equal(expected, doc);
    }

    [Fact]
    public void StateIdsRenderAsInClause()
    {
        var doc = Render(new WorkItemQuery(StateIds: new[] { "submitted", "in-review" }));

        var expected = new BsonDocument("StateId", new BsonDocument("$in",
            new BsonArray { "submitted", "in-review" }));
        Assert.Equal(expected, doc);
    }

    [Fact]
    public void SearchRendersCaseInsensitiveOrAcrossIdAndSubmittedBy()
    {
        var doc = Render(new WorkItemQuery(Search: "  alice  "));

        // Trimmed search needle.
        var pattern = new BsonRegularExpression("alice", "i");
        var or = doc["$or"].AsBsonArray;
        Assert.Equal(2, or.Count);
        Assert.Equal(pattern, or[0]["_id"].AsBsonRegularExpression);
        Assert.Equal(pattern, or[1]["SubmittedBy"].AsBsonRegularExpression);
    }

    [Fact]
    public void SearchEscapesRegexMetacharacters()
    {
        var doc = Render(new WorkItemQuery(Search: "a.b*c"));

        var or = doc["$or"].AsBsonArray;
        var rendered = or[0]["_id"].AsBsonRegularExpression.Pattern;
        // Regex.Escape backslash-escapes the metacharacters; the literal
        // dot must not be interpreted as "any char".
        Assert.Contains(@"\.", rendered);
        Assert.Contains(@"\*", rendered);
    }

    [Fact]
    public void BlankSearchIsIgnored()
    {
        var doc = Render(new WorkItemQuery(Search: "   "));

        Assert.Equal(new BsonDocument(), doc);
    }

    [Fact]
    public void AssigneeIdAloneRendersAsEquality()
    {
        var doc = Render(new WorkItemQuery(AssigneeId: " user-1 "));

        Assert.Equal("user-1", doc["AssignedToId"].AsString);
    }

    [Fact]
    public void UnassignedOnlyAloneRendersAsNullEquality()
    {
        var doc = Render(new WorkItemQuery(UnassignedOnly: true));

        Assert.Equal(BsonNull.Value, doc["AssignedToId"]);
    }

    [Fact]
    public void AssigneeIdWithUnassignedOnlyRendersAsOr()
    {
        var doc = Render(new WorkItemQuery(AssigneeId: "user-1", UnassignedOnly: true));

        var or = doc["$or"].AsBsonArray;
        Assert.Equal(2, or.Count);
        Assert.Equal("user-1", or[0]["AssignedToId"].AsString);
        Assert.Equal(BsonNull.Value, or[1]["AssignedToId"]);
    }

    [Fact]
    public void BlankAssigneeIdIsIgnored()
    {
        var doc = Render(new WorkItemQuery(AssigneeId: "   "));

        Assert.Equal(new BsonDocument(), doc);
    }

    [Fact]
    public void SubmittedByRendersAsEquality()
    {
        var doc = Render(new WorkItemQuery(SubmittedBy: " bob "));

        Assert.Equal("bob", doc["SubmittedBy"].AsString);
    }

    [Fact]
    public void BlankSubmittedByIsIgnored()
    {
        var doc = Render(new WorkItemQuery(SubmittedBy: "   "));

        Assert.Equal(new BsonDocument(), doc);
    }

    [Fact]
    public void MultipleClausesAreCombined()
    {
        // The driver collapses And() over distinct field names into a single
        // flat document; only colliding keys force an explicit $and.
        var doc = Render(new WorkItemQuery(
            TypeIds: new[] { "re-accreditation" },
            StateIds: new[] { "submitted" },
            AssigneeId: "user-1",
            SubmittedBy: "bob"));

        Assert.Equal("re-accreditation", doc["TypeId"]["$in"].AsBsonArray[0].AsString);
        Assert.Equal("submitted", doc["StateId"]["$in"].AsBsonArray[0].AsString);
        Assert.Equal("user-1", doc["AssignedToId"].AsString);
        Assert.Equal("bob", doc["SubmittedBy"].AsString);
    }
}
