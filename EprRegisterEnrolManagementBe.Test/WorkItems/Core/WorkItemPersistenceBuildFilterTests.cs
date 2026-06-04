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
    public void DefaultQueryExcludesApprovedState()
    {
        // IncludeArchived defaults to false — approved items are hidden.
        var doc = Render(new WorkItemQuery());

        Assert.Equal("approved", doc["stateId"]["$ne"].AsString);
    }

    [Fact]
    public void IncludeArchivedTrueRendersEmptyFilter()
    {
        var doc = Render(new WorkItemQuery(IncludeArchived: true));

        Assert.Equal(new BsonDocument(), doc);
    }

    [Fact]
    public void TypeIdsRenderAsInClause()
    {
        // Use IncludeArchived: true to isolate the typeId assertion.
        var doc = Render(new WorkItemQuery(TypeIds: new[] { "re-accreditation", "registration" }, IncludeArchived: true));

        var expected = new BsonDocument("typeId", new BsonDocument("$in",
            new BsonArray { "re-accreditation", "registration" }));
        Assert.Equal(expected, doc);
    }

    [Fact]
    public void StateIdsRenderAsInClause()
    {
        // Use IncludeArchived: true to isolate the stateId assertion.
        var doc = Render(new WorkItemQuery(StateIds: new[] { "submitted", "in-review" }, IncludeArchived: true));

        var expected = new BsonDocument("stateId", new BsonDocument("$in",
            new BsonArray { "submitted", "in-review" }));
        Assert.Equal(expected, doc);
    }

    [Fact]
    public void StateIdsWithArchiveExclusionCombinesBothConditionsOnStateId()
    {
        // The MongoDB C# driver merges $in and $ne on the same field into a
        // single sub-document when combining them with And(), so the rendered
        // filter does NOT use an explicit $and wrapper.
        var doc = Render(new WorkItemQuery(StateIds: new[] { "submitted" }));

        // Both stateId conditions must appear in the rendered document.
        var stateDoc = doc["stateId"].AsBsonDocument;
        Assert.Equal("submitted", stateDoc["$in"].AsBsonArray[0].AsString);
        Assert.Equal("approved", stateDoc["$ne"].AsString);
    }

    [Fact]
    public void StateIdsContainingApprovedSkipsArchiveExclusion()
    {
        // When the caller explicitly filters to StateIds=["approved"],
        // adding $ne:"approved" would make the query unsatisfiable.
        // The exclusion must be omitted in this case.
        var doc = Render(new WorkItemQuery(StateIds: new[] { "approved" }));

        var stateDoc = doc["stateId"].AsBsonDocument;
        Assert.True(stateDoc.Contains("$in"), "Expected $in clause for approved.");
        Assert.False(stateDoc.Contains("$ne"), "$ne:approved must not be added when StateIds already includes approved.");
    }

    [Fact]
    public void StateIdsContainingApprovedAmongOthersSkipsArchiveExclusion()
    {
        var doc = Render(new WorkItemQuery(StateIds: new[] { "approved", "submitted" }));

        var stateDoc = doc["stateId"].AsBsonDocument;
        Assert.False(stateDoc.Contains("$ne"), "$ne:approved must not be added when approved is in StateIds.");
    }

    [Fact]
    public void SearchRendersCaseInsensitiveOrAcrossIdAndSubmittedBy()
    {
        var doc = Render(new WorkItemQuery(Search: "  alice  ", IncludeArchived: true));

        // Trimmed search needle.
        var pattern = new BsonRegularExpression("alice", "i");
        var or = doc["$or"].AsBsonArray;
        Assert.Equal(2, or.Count);
        Assert.Equal(pattern, or[0]["_id"].AsBsonRegularExpression);
        Assert.Equal(pattern, or[1]["submittedBy"].AsBsonRegularExpression);
    }

    [Fact]
    public void SearchEscapesRegexMetacharacters()
    {
        var doc = Render(new WorkItemQuery(Search: "a.b*c", IncludeArchived: true));

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
        var doc = Render(new WorkItemQuery(Search: "   ", IncludeArchived: true));

        Assert.Equal(new BsonDocument(), doc);
    }

    [Fact]
    public void AssigneeIdAloneRendersAsEquality()
    {
        var doc = Render(new WorkItemQuery(AssigneeId: " user-1 ", IncludeArchived: true));

        Assert.Equal("user-1", doc["assignedToId"].AsString);
    }

    [Fact]
    public void UnassignedOnlyAloneRendersAsNullEquality()
    {
        var doc = Render(new WorkItemQuery(UnassignedOnly: true, IncludeArchived: true));

        Assert.Equal(BsonNull.Value, doc["assignedToId"]);
    }

    [Fact]
    public void AssigneeIdWithUnassignedOnlyRendersAsOr()
    {
        var doc = Render(new WorkItemQuery(AssigneeId: "user-1", UnassignedOnly: true, IncludeArchived: true));

        var or = doc["$or"].AsBsonArray;
        Assert.Equal(2, or.Count);
        Assert.Equal("user-1", or[0]["assignedToId"].AsString);
        Assert.Equal(BsonNull.Value, or[1]["assignedToId"]);
    }

    [Fact]
    public void BlankAssigneeIdIsIgnored()
    {
        var doc = Render(new WorkItemQuery(AssigneeId: "   ", IncludeArchived: true));

        Assert.Equal(new BsonDocument(), doc);
    }

    [Fact]
    public void SubmittedByRendersAsEquality()
    {
        var doc = Render(new WorkItemQuery(SubmittedBy: " bob ", IncludeArchived: true));

        Assert.Equal("bob", doc["submittedBy"].AsString);
    }

    [Fact]
    public void BlankSubmittedByIsIgnored()
    {
        var doc = Render(new WorkItemQuery(SubmittedBy: "   ", IncludeArchived: true));

        Assert.Equal(new BsonDocument(), doc);
    }

    [Fact]
    public void MultipleClausesAreCombined()
    {
        // Use IncludeArchived: true so the only stateId clause is the $in from
        // StateIds, which lets the driver collapse everything into a flat doc.
        var doc = Render(new WorkItemQuery(
            TypeIds: new[] { "re-accreditation" },
            StateIds: new[] { "submitted" },
            AssigneeId: "user-1",
            SubmittedBy: "bob",
            IncludeArchived: true));

        Assert.Equal("re-accreditation", doc["typeId"]["$in"].AsBsonArray[0].AsString);
        Assert.Equal("submitted", doc["stateId"]["$in"].AsBsonArray[0].AsString);
        Assert.Equal("user-1", doc["assignedToId"].AsString);
        Assert.Equal("bob", doc["submittedBy"].AsString);
    }

    // ─────────────────────────────── Nations ────────────────────────────────

    [Fact]
    public void NationsRendersAsInClauseOnPayloadNation()
    {
        var doc = Render(new WorkItemQuery(Nations: new[] { "England", "Scotland" }, IncludeArchived: true));

        var inArr = doc["payload.nation"]["$in"].AsBsonArray;
        Assert.Equal(2, inArr.Count);
        Assert.Contains("England", inArr.Select(v => v.AsString));
        Assert.Contains("Scotland", inArr.Select(v => v.AsString));
    }

    [Fact]
    public void SingleNationRendersAsInClause()
    {
        var doc = Render(new WorkItemQuery(Nations: new[] { "Wales" }, IncludeArchived: true));

        Assert.Equal("Wales", doc["payload.nation"]["$in"].AsBsonArray[0].AsString);
    }

    [Fact]
    public void EmptyNationsIsIgnored()
    {
        var doc = Render(new WorkItemQuery(Nations: Array.Empty<string>(), IncludeArchived: true));

        Assert.Equal(new BsonDocument(), doc);
    }

    [Fact]
    public void NullNationsIsIgnored()
    {
        var doc = Render(new WorkItemQuery(Nations: null, IncludeArchived: true));

        Assert.Equal(new BsonDocument(), doc);
    }

    [Fact]
    public void NationsAndTypeIdsCombineCorrectly()
    {
        var doc = Render(new WorkItemQuery(
            TypeIds: new[] { "re-accreditation" },
            Nations: new[] { "England" },
            IncludeArchived: true));

        Assert.Equal("re-accreditation", doc["typeId"]["$in"].AsBsonArray[0].AsString);
        Assert.Equal("England", doc["payload.nation"]["$in"].AsBsonArray[0].AsString);
    }

    // ──────────────────────────── OrgId / RegistrationId / OrgName ──────────────────────────────

    [Fact]
    public void OrgIdRendersAsCaseInsensitiveRegexOnApplicationReference()
    {
        var doc = Render(new WorkItemQuery(OrgId: "  EPR-123  ", IncludeArchived: true));

        var regex = doc["payload.applicationReference"].AsBsonRegularExpression;
        Assert.Contains("EPR-123", regex.Pattern);
        Assert.Equal("i", regex.Options);
    }

    [Fact]
    public void OrgIdEscapesRegexMetacharacters()
    {
        var doc = Render(new WorkItemQuery(OrgId: "a.b*", IncludeArchived: true));

        var pattern = doc["payload.applicationReference"].AsBsonRegularExpression.Pattern;
        Assert.Contains(@"\.", pattern);
        Assert.Contains(@"\*", pattern);
    }

    [Fact]
    public void BlankOrgIdIsIgnored()
    {
        var doc = Render(new WorkItemQuery(OrgId: "   ", IncludeArchived: true));

        Assert.Equal(new BsonDocument(), doc);
    }

    [Fact]
    public void RegistrationIdRendersAsCaseInsensitiveRegexOnId()
    {
        var doc = Render(new WorkItemQuery(RegistrationId: "  abc-123  ", IncludeArchived: true));

        var regex = doc["_id"].AsBsonRegularExpression;
        Assert.Contains("abc-123", regex.Pattern);
        Assert.Equal("i", regex.Options);
    }

    [Fact]
    public void BlankRegistrationIdIsIgnored()
    {
        var doc = Render(new WorkItemQuery(RegistrationId: "   ", IncludeArchived: true));

        Assert.Equal(new BsonDocument(), doc);
    }

    [Fact]
    public void OrgNameRendersAsTextSearchPhrase()
    {
        var doc = Render(new WorkItemQuery(OrgName: "  Acme Ltd  ", IncludeArchived: true));

        // Quoted phrase prevents OR word-matching against common words.
        Assert.Equal("\"Acme Ltd\"", doc["$text"]["$search"].AsString);
    }

    [Fact]
    public void BlankOrgNameIsIgnored()
    {
        var doc = Render(new WorkItemQuery(OrgName: "   ", IncludeArchived: true));

        Assert.Equal(new BsonDocument(), doc);
    }
}