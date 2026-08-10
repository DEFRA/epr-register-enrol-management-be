using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.Utils.Mongo;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// epr-2uxy. The discriminator under test decides whether a regulator sees a
/// genuinely new overseas site, so it gets exhaustive coverage rather than a
/// happy path: a false negative here leaves a spurious badge (recoverable), but
/// a false positive would let the correction migration hide a real new site
/// (not recoverable, and invisible).
/// </summary>
public class ReAccreditationIsNewSiteAuditTests
{
    private static BsonDocument Site(params (string Key, BsonValue Value)[] fields)
    {
        var doc = new BsonDocument();
        foreach (var (key, value) in fields)
        {
            doc[key] = value;
        }

        return doc;
    }

    /// <summary>
    /// Exactly the field set <c>HttpReExApiAdapter.MapOverseasSite</c> populates
    /// — no orsId, and none of the operator-entered detail fields.
    /// </summary>
    private static BsonDocument ReExSite(bool isNewSite) => Site(
        ("siteName", "ReEx Site"),
        ("siteAddress", "1 ReEx Way"),
        ("country", "Netherlands"),
        ("isEu", true),
        ("isOecd", true),
        ("selected", false),
        ("isNewSite", isNewSite));

    private static BsonDocument WorkItemDoc(string id, DateTime submittedAt, params BsonDocument[] sites) =>
        new()
        {
            ["_id"] = id,
            ["typeId"] = ReAccreditationType.Id,
            ["submittedAt"] = submittedAt,
            ["payload"] = new BsonDocument
            {
                ["applicationReference"] = $"RA-{id}",
                ["organisationName"] = $"Org {id}",
                ["overseasSites"] = new BsonDocument { ["sites"] = new BsonArray(sites) }
            }
        };

    // ── ClassifySite: the discriminator ──────────────────────────────────────

    [Fact]
    public void ClassifySite_returns_not_flagged_new_when_isNewSite_is_false_or_absent()
    {
        Assert.Equal(
            ReAccreditationIsNewSiteAudit.SiteVerdict.NotFlaggedNew,
            ReAccreditationIsNewSiteAudit.ClassifySite(ReExSite(isNewSite: false)));

        // No isNewSite key at all — a pre-transmission site.
        Assert.Equal(
            ReAccreditationIsNewSiteAudit.SiteVerdict.NotFlaggedNew,
            ReAccreditationIsNewSiteAudit.ClassifySite(Site(("siteName", "X"))));
    }

    [Fact]
    public void ClassifySite_returns_operator_added_when_orsId_is_present()
    {
        // orsId is required and NotEmpty-validated on the operator side, so its
        // presence proves the site came through AddOverseasSite, not ReEx —
        // which means isNewSite: true is the genuine value.
        var verdict = ReAccreditationIsNewSiteAudit.ClassifySite(
            Site(("orsId", "ORS-2026-0292"), ("siteName", "Operator Site"), ("isNewSite", true)));

        Assert.Equal(
            ReAccreditationIsNewSiteAudit.SiteVerdict.OperatorAddedCorrect, verdict);
    }

    [Fact]
    public void ClassifySite_returns_provably_corrupt_for_a_reex_shaped_site_flagged_new()
    {
        // The whole remediation rests on this case: ReEx sets IsNewSite = false
        // explicitly and has never set OrsId, so a ReEx-shaped site reading true
        // is a defaulted value, not a real one.
        Assert.Equal(
            ReAccreditationIsNewSiteAudit.SiteVerdict.ProvablyCorrupt,
            ReAccreditationIsNewSiteAudit.ClassifySite(ReExSite(isNewSite: true)));
    }

    [Theory]
    [InlineData("contactName")]
    [InlineData("contactEmail")]
    [InlineData("contactPhone")]
    [InlineData("operationCode")]
    [InlineData("code1")]
    [InlineData("code2")]
    [InlineData("code3")]
    [InlineData("addressLine1")]
    [InlineData("addressLine2")]
    [InlineData("townOrCity")]
    [InlineData("coordinates")]
    [InlineData("repatriatedLoads")]
    [InlineData("conditionsOfExport")]
    public void ClassifySite_returns_ambiguous_when_orsId_missing_but_operator_detail_present(
        string detailField)
    {
        // The safety valve for the one caveat on the discriminator: orsId was
        // historically client-clobberable, so a stripped orsId would make an
        // operator-added site look ReEx-sourced. ReEx-mapped sites carry none of
        // these fields, so their presence means "do not touch this".
        var site = ReExSite(isNewSite: true);
        site[detailField] = "something";

        Assert.Equal(
            ReAccreditationIsNewSiteAudit.SiteVerdict.AmbiguousOrsIdMissing,
            ReAccreditationIsNewSiteAudit.ClassifySite(site));
    }

    [Fact]
    public void ClassifySite_returns_ambiguous_for_nested_operator_detail_objects()
    {
        // besEvidence and interimSite are objects rather than scalars; both are
        // things ReEx never produces.
        var withBes = ReExSite(isNewSite: true);
        withBes["besEvidence"] = new BsonDocument { ["files"] = new BsonArray() };

        var withInterim = ReExSite(isNewSite: true);
        withInterim["interimSite"] = new BsonDocument { ["isNewSite"] = true };

        Assert.Equal(
            ReAccreditationIsNewSiteAudit.SiteVerdict.AmbiguousOrsIdMissing,
            ReAccreditationIsNewSiteAudit.ClassifySite(withBes));
        Assert.Equal(
            ReAccreditationIsNewSiteAudit.SiteVerdict.AmbiguousOrsIdMissing,
            ReAccreditationIsNewSiteAudit.ClassifySite(withInterim));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ClassifySite_treats_a_blank_orsId_as_absent(string blank)
    {
        // The operator validator forbids empty, and WhenWritingNull omits null —
        // so a blank orsId is not a legitimate operator-added shape. Treating it
        // as "present" would let a corrupt site escape classification entirely.
        var site = ReExSite(isNewSite: true);
        site["orsId"] = blank;

        Assert.Equal(
            ReAccreditationIsNewSiteAudit.SiteVerdict.ProvablyCorrupt,
            ReAccreditationIsNewSiteAudit.ClassifySite(site));
    }

    [Fact]
    public void ClassifySite_treats_an_explicit_null_orsId_as_absent()
    {
        var site = ReExSite(isNewSite: true);
        site["orsId"] = BsonNull.Value;

        Assert.Equal(
            ReAccreditationIsNewSiteAudit.SiteVerdict.ProvablyCorrupt,
            ReAccreditationIsNewSiteAudit.ClassifySite(site));
    }

    // ── Classify: aggregation ────────────────────────────────────────────────

    [Fact]
    public void Classify_buckets_items_by_verdict_and_counts_sites()
    {
        var now = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);
        var ambiguousSite = ReExSite(isNewSite: true);
        ambiguousSite["operationCode"] = "R3";

        var result = ReAccreditationIsNewSiteAudit.Classify(
        [
            WorkItemDoc("corrupt", now, ReExSite(isNewSite: true)),
            WorkItemDoc("ok", now,
                Site(("orsId", "ORS-1"), ("siteName", "Op"), ("isNewSite", true))),
            WorkItemDoc("ambiguous", now, ambiguousSite),
            WorkItemDoc("clean", now, ReExSite(isNewSite: false))
        ]);

        Assert.Equal(4, result.ItemsScanned);
        Assert.Equal(1, result.SitesProvablyCorrupt);
        Assert.Equal(1, result.SitesAmbiguous);
        Assert.Equal(1, result.SitesOperatorAddedCorrect);
        Assert.Equal(1, result.SitesNotFlaggedNew);
        Assert.Equal("corrupt", Assert.Single(result.ItemsWithProvablyCorrupt).Id);
        Assert.Equal("ambiguous", Assert.Single(result.ItemsWithAmbiguous).Id);
    }

    [Fact]
    public void Classify_reports_an_item_carrying_both_a_corrupt_and_an_ambiguous_site_in_both_buckets()
    {
        // One item can need both automated correction and manual adjudication.
        // Bucketing it into only one would either lose the correctable site or
        // silently sweep the ambiguous one into the migration's path.
        var ambiguousSite = ReExSite(isNewSite: true);
        ambiguousSite["contactName"] = "Someone";

        var result = ReAccreditationIsNewSiteAudit.Classify(
        [
            WorkItemDoc("both", new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc),
                ReExSite(isNewSite: true), ambiguousSite)
        ]);

        Assert.Single(result.ItemsWithProvablyCorrupt);
        Assert.Single(result.ItemsWithAmbiguous);
        Assert.Equal(1, result.SitesProvablyCorrupt);
        Assert.Equal(1, result.SitesAmbiguous);
    }

    [Fact]
    public void Classify_tolerates_items_with_no_payload_or_no_sites()
    {
        var result = ReAccreditationIsNewSiteAudit.Classify(
        [
            new BsonDocument { ["_id"] = "no-payload" },
            new BsonDocument { ["_id"] = "empty-payload", ["payload"] = new BsonDocument() },
            new BsonDocument
            {
                ["_id"] = "sites-not-array",
                ["payload"] = new BsonDocument
                {
                    ["overseasSites"] = new BsonDocument { ["sites"] = "nope" }
                }
            },
            new BsonDocument
            {
                ["_id"] = "overseas-not-doc",
                ["payload"] = new BsonDocument { ["overseasSites"] = "nope" }
            }
        ]);

        Assert.Equal(4, result.ItemsScanned);
        Assert.Empty(result.ItemsWithProvablyCorrupt);
        Assert.Empty(result.ItemsWithAmbiguous);
    }

    [Fact]
    public void Classify_ignores_non_document_entries_in_the_sites_array()
    {
        var doc = WorkItemDoc(
            "mixed", new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc),
            ReExSite(isNewSite: true));
        doc["payload"]["overseasSites"]["sites"].AsBsonArray.Add("not-a-document");

        var result = ReAccreditationIsNewSiteAudit.Classify([doc]);

        Assert.Equal(1, result.SitesProvablyCorrupt);
    }

    [Fact]
    public void Classify_reports_missing_identifiers_rather_than_throwing()
    {
        // Run against production data, so a document missing applicationReference
        // or organisationName must degrade to a placeholder, not abort the audit
        // and lose every other id in the report.
        var result = ReAccreditationIsNewSiteAudit.Classify(
        [
            new BsonDocument
            {
                ["_id"] = "sparse",
                ["payload"] = new BsonDocument
                {
                    ["overseasSites"] = new BsonDocument
                    {
                        ["sites"] = new BsonArray { ReExSite(isNewSite: true) }
                    }
                }
            }
        ]);

        var row = Assert.Single(result.ItemsWithProvablyCorrupt);
        Assert.Equal("(none)", row.ApplicationReference);
        Assert.Equal("(none)", row.OrganisationName);
    }

    // ── The IO path, against real MongoDB ────────────────────────────────────

    [Fact]
    public async Task RunAsync_applies_the_date_and_type_bounds_and_writes_nothing()
    {
        // Read-only is asserted the strong way — by comparing the entire
        // collection before and after — rather than by asserting which methods
        // were called on a mock. A diagnostic that could write would be a worse
        // thing to point at production than the defect it measures.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new MongoIntegrationFixture();
        await fixture.InitializeAsync();

        var client = new MongoClient(fixture.ConnectionString);
        var database = client.GetDatabase(MongoIntegrationFixture.NewDatabaseName("audit"));
        var collection = database.GetCollection<BsonDocument>("workItems");

        var inWindow = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);
        var beforeWindow = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var afterWindow = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        var wrongType = WorkItemDoc("wrong-type", inWindow, ReExSite(isNewSite: true));
        wrongType["typeId"] = "some-other-type";

        var noSites = new BsonDocument
        {
            ["_id"] = "no-sites",
            ["typeId"] = ReAccreditationType.Id,
            ["submittedAt"] = inWindow,
            ["payload"] = new BsonDocument { ["organisationName"] = "No Sites Ltd" }
        };

        await collection.InsertManyAsync(
            [
                WorkItemDoc("in-window", inWindow, ReExSite(isNewSite: true)),
                // Pre-transmission: cannot carry the defect, must be excluded
                // even though its shape matches.
                WorkItemDoc("before-window", beforeWindow, ReExSite(isNewSite: true)),
                // Post-deploy: the operator default is fixed by then.
                WorkItemDoc("after-window", afterWindow, ReExSite(isNewSite: true)),
                wrongType,
                noSites
            ],
            cancellationToken: ct);

        var before = await collection.Find(FilterDefinition<BsonDocument>.Empty)
            .SortBy(d => d["_id"]).ToListAsync(ct);

        var result = await ReAccreditationIsNewSiteAudit.RunAsync(
            collection,
            NullLogger.Instance,
            windowEnd: new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc),
            cancellationToken: ct);

        Assert.Equal(1, result.ItemsScanned);
        Assert.Equal("in-window", Assert.Single(result.ItemsWithProvablyCorrupt).Id);

        var after = await collection.Find(FilterDefinition<BsonDocument>.Empty)
            .SortBy(d => d["_id"]).ToListAsync(ct);
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(
            before.Select(d => d.ToJson()).ToList(),
            after.Select(d => d.ToJson()).ToList());

        await client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName, ct);
    }

    [Fact]
    public async Task RunAsync_reports_ambiguous_sites_separately_from_correctable_ones()
    {
        // The ambiguous set is the one a careless correction would ruin, so it
        // has to survive the IO path into its own bucket rather than being
        // folded in with the correctable ids.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new MongoIntegrationFixture();
        await fixture.InitializeAsync();

        var client = new MongoClient(fixture.ConnectionString);
        var database = client.GetDatabase(MongoIntegrationFixture.NewDatabaseName("audit-amb"));
        var collection = database.GetCollection<BsonDocument>("workItems");

        var ambiguousSite = ReExSite(isNewSite: true);
        ambiguousSite["operationCode"] = "R3";

        await collection.InsertManyAsync(
            [
                WorkItemDoc("corrupt", new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc),
                    ReExSite(isNewSite: true)),
                WorkItemDoc("ambiguous", new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc),
                    ambiguousSite)
            ],
            cancellationToken: ct);

        var result = await ReAccreditationIsNewSiteAudit.RunAsync(
            collection,
            NullLogger.Instance,
            windowEnd: new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc),
            cancellationToken: ct);

        Assert.Equal("corrupt", Assert.Single(result.ItemsWithProvablyCorrupt).Id);
        Assert.Equal("ambiguous", Assert.Single(result.ItemsWithAmbiguous).Id);

        await client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName, ct);
    }

    [Fact]
    public async Task RunAsync_from_the_service_provider_is_a_no_op_unless_enabled()
    {
        // Registered on the startup harness in every environment, so "not
        // configured" must cost nothing — it must not even resolve the Mongo
        // client, let alone query.
        var ct = TestContext.Current.CancellationToken;
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(
                new ConfigurationBuilder().AddInMemoryCollection([]).Build())
            .BuildServiceProvider();

        // No IMongoDbClientFactory registered: if the gate leaked, resolving it
        // would throw rather than silently pass.
        await ReAccreditationIsNewSiteAudit.RunAsync(services, NullLogger.Instance, ct);
    }

    [Fact]
    public async Task RunAsync_from_the_service_provider_runs_when_enabled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new MongoIntegrationFixture();
        await fixture.InitializeAsync();

        var databaseName = MongoIntegrationFixture.NewDatabaseName("audit-di");
        var factory = new TestMongoDbClientFactory(fixture.ConnectionString, databaseName);
        await factory.GetCollection<BsonDocument>("workItems").InsertOneAsync(
            WorkItemDoc(
                "in-window",
                new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc),
                ReExSite(isNewSite: true)),
            cancellationToken: ct);

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Deliberately no DeployedAt: omitting it is the likely
                    // first run, and the window end must then default to now
                    // rather than collapsing the window to nothing.
                    [ReAccreditationIsNewSiteAudit.EnabledConfigKey] = "true"
                })
                .Build())
            .AddSingleton<IMongoDbClientFactory>(factory)
            .BuildServiceProvider();

        await ReAccreditationIsNewSiteAudit.RunAsync(services, NullLogger.Instance, ct);

        // Reached Mongo and left it alone.
        var after = await factory.GetCollection<BsonDocument>("workItems")
            .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(ct);
        Assert.True(after.Single()["payload"]["overseasSites"]["sites"][0]["isNewSite"].AsBoolean);

        factory.GetClient().DropDatabase(databaseName);
    }

    [Fact]
    public async Task RunAsync_from_the_service_provider_honours_a_configured_deploy_time()
    {
        // Supplying the deploy time is what makes the upper bound precise rather
        // than "now", so an item submitted after the deploy must fall outside it.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new MongoIntegrationFixture();
        await fixture.InitializeAsync();

        var databaseName = MongoIntegrationFixture.NewDatabaseName("audit-deployedat");
        var factory = new TestMongoDbClientFactory(fixture.ConnectionString, databaseName);
        await factory.GetCollection<BsonDocument>("workItems").InsertOneAsync(
            WorkItemDoc(
                "after-deploy",
                new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc),
                ReExSite(isNewSite: true)),
            cancellationToken: ct);

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [ReAccreditationIsNewSiteAudit.EnabledConfigKey] = "true",
                    [ReAccreditationIsNewSiteAudit.DeployedAtConfigKey] = "2026-08-15T00:00:00Z"
                })
                .Build())
            .AddSingleton<IMongoDbClientFactory>(factory)
            .BuildServiceProvider();

        await ReAccreditationIsNewSiteAudit.RunAsync(services, NullLogger.Instance, ct);

        var after = await factory.GetCollection<BsonDocument>("workItems")
            .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(ct);
        Assert.True(after.Single()["payload"]["overseasSites"]["sites"][0]["isNewSite"].AsBoolean);

        factory.GetClient().DropDatabase(databaseName);
    }

    [Fact]
    public async Task RunAsync_reports_a_clean_environment_without_writing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new MongoIntegrationFixture();
        await fixture.InitializeAsync();

        var client = new MongoClient(fixture.ConnectionString);
        var database = client.GetDatabase(MongoIntegrationFixture.NewDatabaseName("audit-clean"));
        var collection = database.GetCollection<BsonDocument>("workItems");

        await collection.InsertOneAsync(
            WorkItemDoc(
                "clean",
                new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc),
                ReExSite(isNewSite: false)),
            cancellationToken: ct);

        var result = await ReAccreditationIsNewSiteAudit.RunAsync(
            collection,
            NullLogger.Instance,
            windowEnd: new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc),
            cancellationToken: ct);

        Assert.Equal(0, result.SitesProvablyCorrupt);
        Assert.Equal(0, result.SitesAmbiguous);
        Assert.Empty(result.ItemsWithProvablyCorrupt);

        await client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName, ct);
    }
}
