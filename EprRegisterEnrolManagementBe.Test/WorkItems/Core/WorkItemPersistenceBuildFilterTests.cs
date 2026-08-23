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
    private static readonly string[] s_materialsPlastic = ["plastic"];
    private static readonly string[] s_materialsPlasticAndGlass = ["plastic", "glass"];
    private static readonly string[] s_materialsWithRegexMetacharacter = ["a.b"];
    private static readonly string[] s_nationsEngland = ["England"];
    private static readonly string[] s_nationsEnglandAndScotland = ["England", "Scotland"];
    private static readonly string[] s_nationsWales = ["Wales"];
    private static readonly string[] s_stateIdsSubmitted = ["submitted"];
    private static readonly string[] s_stateIdsSubmittedAndInReview = ["submitted", "in-review"];
    private static readonly string[] s_stateIdsWithdrawnAndSubmitted = ["withdrawn", "submitted"];
    private static readonly string[] s_typeIdsReAccreditation = ["re-accreditation"];
    private static readonly string[] s_typeIdsReAccreditationAndRegistration =
    [
        "re-accreditation",
        "registration",
    ];
    private static readonly string[] s_wasteProcessingTypesBothBuckets =
    [
        "exporter",
        "reprocessor",
    ];
    private static readonly string[] s_wasteProcessingTypesExporter = ["exporter"];
    private static readonly string[] s_wasteProcessingTypesExporterMixedCase = ["Exporter"];
    private static readonly string[] s_wasteProcessingTypesReprocessor = ["reprocessor"];
    private static readonly string[] s_wasteProcessingTypesUnknown = ["bogus"];

    private static readonly IBsonSerializer<WorkItem> s_workItemSerializer =
        BsonSerializer.SerializerRegistry.GetSerializer<WorkItem>();

    private static BsonDocument Render(WorkItemQuery query)
    {
        var filter = WorkItemPersistence.BuildFilter(query);
        return filter.Render(new RenderArgs<WorkItem>(s_workItemSerializer, BsonSerializer.SerializerRegistry));
    }

    [Fact]
    public void DefaultQueryFiltersNothing()
    {
        // RA-313: a bare query renders an EMPTY filter. Before RA-313 this
        // emitted a $nin excluding every terminal state, which is exactly what
        // kept withdrawn applications off the regulator's worklist.
        var doc = Render(new WorkItemQuery());

        Assert.Equal(new BsonDocument(), doc);
    }

    [Fact]
    public void IncludeArchivedMakesNoDifferenceToTheFilter()
    {
        // RA-313 retains IncludeArchived on the query (management-fe still
        // sends it, and the migrations pass true) but it no longer selects
        // anything. Both values must render identically — if this ever fails,
        // an archive exclusion has crept back in.
        Assert.Equal(
            Render(new WorkItemQuery(IncludeArchived: true)),
            Render(new WorkItemQuery(IncludeArchived: false)));
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    public void TerminalStateIsNeverExcluded(string terminalStateId)
    {
        // RA-313 AC01, at the filter level: nothing anywhere in the rendered
        // filter may exclude a terminal state, with or without IncludeArchived.
        foreach (var includeArchived in new[] { true, false })
        {
            var doc = Render(new WorkItemQuery(IncludeArchived: includeArchived));

            Assert.DoesNotContain(terminalStateId, doc.ToJson());
        }
    }

    [Fact]
    public void TypeIdsRenderAsInClause()
    {
        var doc = Render(new WorkItemQuery(TypeIds: s_typeIdsReAccreditationAndRegistration));

        var expected = new BsonDocument("typeId", new BsonDocument("$in",
            new BsonArray { "re-accreditation", "registration" }));
        Assert.Equal(expected, doc);
    }

    [Fact]
    public void StateIdsRenderAsInClause()
    {
        // RA-313: filtering to non-terminal states used to ALSO emit a $nin of
        // every terminal state, so this could only assert the whole document
        // once IncludeArchived: true had suppressed it. The caller's selection
        // is now the entire stateId clause — what you ask for is what you get.
        var doc = Render(new WorkItemQuery(StateIds: s_stateIdsSubmittedAndInReview));

        var expected = new BsonDocument("stateId", new BsonDocument("$in",
            new BsonArray { "submitted", "in-review" }));
        Assert.Equal(expected, doc);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    public void FilteringToASingleTerminalStateRendersThatStateAlone(string terminalStateId)
    {
        // The "Withdrawn" status checkbox on the Applications page lands here.
        // It worked before RA-313 only because of a special case that spared
        // explicitly-requested terminal states from the $nin; with the
        // exclusion gone it is now just an ordinary $in.
        var doc = Render(new WorkItemQuery(StateIds: new[] { terminalStateId }));

        var stateDoc = doc["stateId"].AsBsonDocument;
        Assert.Equal(terminalStateId, stateDoc["$in"].AsBsonArray[0].AsString);
        Assert.False(stateDoc.Contains("$nin"), "RA-313: no terminal-state exclusion.");
    }

    [Fact]
    public void MixedTerminalAndActiveStateIdsRenderAsOneInClause()
    {
        var doc = Render(new WorkItemQuery(StateIds: s_stateIdsWithdrawnAndSubmitted));

        var selected = doc["stateId"]["$in"].AsBsonArray.Select(v => v.AsString).ToList();
        Assert.Equal(s_stateIdsWithdrawnAndSubmitted, selected);
        Assert.False(doc["stateId"].AsBsonDocument.Contains("$nin"), "RA-313: no terminal-state exclusion.");
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
        Assert.Equal(pattern, or[1]["submittedBy"].AsBsonRegularExpression);
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

        Assert.Equal("user-1", doc["assignedToId"].AsString);
    }

    [Fact]
    public void UnassignedOnlyAloneRendersAsNullEquality()
    {
        var doc = Render(new WorkItemQuery(UnassignedOnly: true));

        Assert.Equal(BsonNull.Value, doc["assignedToId"]);
    }

    [Fact]
    public void AssigneeIdWithUnassignedOnlyRendersAsOr()
    {
        var doc = Render(new WorkItemQuery(AssigneeId: "user-1", UnassignedOnly: true));

        var or = doc["$or"].AsBsonArray;
        Assert.Equal(2, or.Count);
        Assert.Equal("user-1", or[0]["assignedToId"].AsString);
        Assert.Equal(BsonNull.Value, or[1]["assignedToId"]);
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

        Assert.Equal("bob", doc["submittedBy"].AsString);
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
        var doc = Render(new WorkItemQuery(
            TypeIds: s_typeIdsReAccreditation,
            StateIds: s_stateIdsSubmitted,
            AssigneeId: "user-1",
            SubmittedBy: "bob"));

        Assert.Equal("re-accreditation", doc["typeId"]["$in"].AsBsonArray[0].AsString);
        Assert.Equal("submitted", doc["stateId"]["$in"].AsBsonArray[0].AsString);
        Assert.Equal("user-1", doc["assignedToId"].AsString);
        Assert.Equal("bob", doc["submittedBy"].AsString);
    }

    // ─────────────────────────────── Nations ────────────────────────────────

    [Fact]
    public void NationsRendersAsInClauseOnPayloadNation()
    {
        var doc = Render(new WorkItemQuery(Nations: s_nationsEnglandAndScotland));

        var inArr = doc["payload.nation"]["$in"].AsBsonArray;
        Assert.Equal(2, inArr.Count);
        Assert.Contains("England", inArr.Select(v => v.AsString));
        Assert.Contains("Scotland", inArr.Select(v => v.AsString));
    }

    [Fact]
    public void SingleNationRendersAsInClause()
    {
        var doc = Render(new WorkItemQuery(Nations: s_nationsWales));

        Assert.Equal("Wales", doc["payload.nation"]["$in"].AsBsonArray[0].AsString);
    }

    [Fact]
    public void EmptyNationsIsIgnored()
    {
        var doc = Render(new WorkItemQuery(Nations: Array.Empty<string>()));

        Assert.Equal(new BsonDocument(), doc);
    }

    [Fact]
    public void NullNationsIsIgnored()
    {
        var doc = Render(new WorkItemQuery(Nations: null));

        Assert.Equal(new BsonDocument(), doc);
    }

    [Fact]
    public void NationsAndTypeIdsCombineCorrectly()
    {
        var doc = Render(new WorkItemQuery(
            TypeIds: s_typeIdsReAccreditation,
            Nations: s_nationsEngland));

        Assert.Equal("re-accreditation", doc["typeId"]["$in"].AsBsonArray[0].AsString);
        Assert.Equal("England", doc["payload.nation"]["$in"].AsBsonArray[0].AsString);
    }

    // ──────────────────────────── OrgId / RegistrationId / OrgName ──────────────────────────────

    [Fact]
    public void OrgIdRendersAsCaseInsensitiveRegexOnApplicationReference()
    {
        var doc = Render(new WorkItemQuery(OrgId: "  EPR-123  "));

        var regex = doc["payload.applicationReference"].AsBsonRegularExpression;
        Assert.Contains("EPR-123", regex.Pattern);
        Assert.Equal("i", regex.Options);
    }

    [Fact]
    public void OrgIdEscapesRegexMetacharacters()
    {
        var doc = Render(new WorkItemQuery(OrgId: "a.b*"));

        var pattern = doc["payload.applicationReference"].AsBsonRegularExpression.Pattern;
        Assert.Contains(@"\.", pattern);
        Assert.Contains(@"\*", pattern);
    }

    [Fact]
    public void BlankOrgIdIsIgnored()
    {
        var doc = Render(new WorkItemQuery(OrgId: "   "));

        Assert.Equal(new BsonDocument(), doc);
    }

    [Fact]
    public void RegistrationIdRendersAsCaseInsensitiveRegexOnId()
    {
        var doc = Render(new WorkItemQuery(RegistrationId: "  abc-123  "));

        var regex = doc["_id"].AsBsonRegularExpression;
        Assert.Contains("abc-123", regex.Pattern);
        Assert.Equal("i", regex.Options);
    }

    [Fact]
    public void BlankRegistrationIdIsIgnored()
    {
        var doc = Render(new WorkItemQuery(RegistrationId: "   "));

        Assert.Equal(new BsonDocument(), doc);
    }

    [Fact]
    public void OrgNameRendersAsTextSearchPhrase()
    {
        var doc = Render(new WorkItemQuery(OrgName: "  Acme Ltd  "));

        // Quoted phrase prevents OR word-matching against common words.
        Assert.Equal("\"Acme Ltd\"", doc["$text"]["$search"].AsString);
    }

    [Fact]
    public void BlankOrgNameIsIgnored()
    {
        var doc = Render(new WorkItemQuery(OrgName: "   "));

        Assert.Equal(new BsonDocument(), doc);
    }

    // ──────────────────────────── Organisation (name or ID) ─────────────────────────────

    [Fact]
    public void OrganisationRendersAsCaseInsensitiveOrAcrossNameAndOperatorOrgId()
    {
        var doc = Render(new WorkItemQuery(Organisation: "  Acme  "));

        var or = doc["$or"].AsBsonArray;
        Assert.Equal(2, or.Count);
        var nameRegex = or[0]["payload.organisationName"].AsBsonRegularExpression;
        var idRegex = or[1]["payload.operatorOrganisationId"].AsBsonRegularExpression;
        // Trimmed, case-insensitive substring (not anchored) on both fields.
        Assert.Equal("Acme", nameRegex.Pattern);
        Assert.Equal("i", nameRegex.Options);
        Assert.Equal("Acme", idRegex.Pattern);
        Assert.Equal("i", idRegex.Options);
    }

    [Fact]
    public void OrganisationDoesNotMatchRegistrationIdOrWorkItemId()
    {
        // RA-324: reg-id is dropped from the combined box — it must not add an
        // _id clause (that would resurrect registration-id findability).
        var doc = Render(new WorkItemQuery(Organisation: "ORG-123"));

        var or = doc["$or"].AsBsonArray;
        Assert.All(or, clause => Assert.False(clause.AsBsonDocument.Contains("_id")));
    }

    [Fact]
    public void OrganisationEscapesRegexMetacharacters()
    {
        var doc = Render(new WorkItemQuery(Organisation: "a.b*"));

        var pattern = doc["$or"][0]["payload.organisationName"].AsBsonRegularExpression.Pattern;
        Assert.Contains(@"\.", pattern);
        Assert.Contains(@"\*", pattern);
    }

    [Fact]
    public void BlankOrganisationIsIgnored()
    {
        var doc = Render(new WorkItemQuery(Organisation: "   "));

        Assert.Equal(new BsonDocument(), doc);
    }

    // ──────────────────────────────── Materials ─────────────────────────────

    [Fact]
    public void SingleMaterialRendersAsAnchoredCaseInsensitiveRegex()
    {
        var doc = Render(new WorkItemQuery(Materials: s_materialsPlastic));

        var regex = doc["payload.material"].AsBsonRegularExpression;
        // Anchored so it is an exact-token match, "i" so casing never hides it.
        Assert.Equal("^plastic$", regex.Pattern);
        Assert.Equal("i", regex.Options);
    }

    [Fact]
    public void MultipleMaterialsRenderAsOrOfAnchoredRegexes()
    {
        var doc = Render(new WorkItemQuery(
            Materials: s_materialsPlasticAndGlass));

        var or = doc["$or"].AsBsonArray;
        Assert.Equal(2, or.Count);
        Assert.Equal("^plastic$", or[0]["payload.material"].AsBsonRegularExpression.Pattern);
        Assert.Equal("^glass$", or[1]["payload.material"].AsBsonRegularExpression.Pattern);
    }

    [Fact]
    public void MaterialEscapesRegexMetacharacters()
    {
        var doc = Render(new WorkItemQuery(Materials: s_materialsWithRegexMetacharacter));

        var pattern = doc["payload.material"].AsBsonRegularExpression.Pattern;
        Assert.Equal(@"^a\.b$", pattern);
    }

    [Fact]
    public void EmptyMaterialsIsIgnored()
    {
        var doc = Render(new WorkItemQuery(Materials: Array.Empty<string>()));

        Assert.Equal(new BsonDocument(), doc);
    }

    [Fact]
    public void NullMaterialsIsIgnored()
    {
        var doc = Render(new WorkItemQuery(Materials: null));

        Assert.Equal(new BsonDocument(), doc);
    }

    // ───────────────────────── WasteProcessingType (RA-412) ─────────────────────────

    [Fact]
    public void ExporterRendersAsAnchoredCaseInsensitiveRegex()
    {
        var doc = Render(new WorkItemQuery(WasteProcessingTypes: s_wasteProcessingTypesExporter));

        var regex = doc["payload.wasteProcessingType"].AsBsonRegularExpression;
        Assert.Equal("^exporter$", regex.Pattern);
        Assert.Equal("i", regex.Options);
    }

    [Fact]
    public void ExporterValueIsCaseInsensitive()
    {
        // Casing of the QUERY VALUE must not change which bucket it lands in
        // ("Exporter" still means exporter) — the rendered pattern is always
        // the fixed literal "^exporter$"; only the match against stored data
        // is case-insensitive ("i" option).
        var doc = Render(new WorkItemQuery(WasteProcessingTypes: s_wasteProcessingTypesExporterMixedCase));

        Assert.Equal("^exporter$", doc["payload.wasteProcessingType"].AsBsonRegularExpression.Pattern);
    }

    [Fact]
    public void ReprocessorRendersAsNotExporter()
    {
        // Self-review fix: "reprocessor" must NOT be a literal-string match —
        // that would exclude every item with no wasteProcessingType at all,
        // disagreeing with the "absent means Reprocessor" fallback every
        // other reader of this field applies. $not on the exporter regex
        // matches everything that isn't a literal "exporter", including an
        // absent or non-string field (confirmed against a real Mongo instance
        // in WorkItemPersistenceMongoIntegrationTests).
        var doc = Render(new WorkItemQuery(WasteProcessingTypes: s_wasteProcessingTypesReprocessor));

        var not = doc["payload.wasteProcessingType"]["$not"];
        Assert.Equal("^exporter$", not.AsBsonRegularExpression.Pattern);
        Assert.Equal("i", not.AsBsonRegularExpression.Options);
    }

    [Fact]
    public void UnknownValueIsTreatedAsReprocessor()
    {
        // No backend enum for this field: anything that isn't literally
        // "exporter" falls into the same "not exporter" bucket as
        // "reprocessor" itself — there is no third bucket to drop the value
        // into, and that mirrors the app-wide "anything but exporter" rule.
        var doc = Render(new WorkItemQuery(WasteProcessingTypes: s_wasteProcessingTypesUnknown));

        Assert.Equal(
            "^exporter$",
            doc["payload.wasteProcessingType"]["$not"].AsBsonRegularExpression.Pattern);
    }

    [Fact]
    public void BothBucketsSelectedAppliesNoRestriction()
    {
        // Exporter and "not exporter" partition every possible item, so
        // selecting both is equivalent to selecting neither.
        var doc = Render(new WorkItemQuery(
            WasteProcessingTypes: s_wasteProcessingTypesBothBuckets));

        Assert.Equal(new BsonDocument(), doc);
    }

    [Fact]
    public void EmptyWasteProcessingTypesIsIgnored()
    {
        var doc = Render(new WorkItemQuery(WasteProcessingTypes: Array.Empty<string>()));

        Assert.Equal(new BsonDocument(), doc);
    }

    [Fact]
    public void NullWasteProcessingTypesIsIgnored()
    {
        var doc = Render(new WorkItemQuery(WasteProcessingTypes: null));

        Assert.Equal(new BsonDocument(), doc);
    }

    [Fact]
    public void WasteProcessingTypesAndTypeIdsCombineCorrectly()
    {
        var doc = Render(new WorkItemQuery(
            TypeIds: s_typeIdsReAccreditation,
            WasteProcessingTypes: s_wasteProcessingTypesExporter));

        Assert.Equal("re-accreditation", doc["typeId"]["$in"].AsBsonArray[0].AsString);
        Assert.Equal("^exporter$", doc["payload.wasteProcessingType"].AsBsonRegularExpression.Pattern);
    }
}