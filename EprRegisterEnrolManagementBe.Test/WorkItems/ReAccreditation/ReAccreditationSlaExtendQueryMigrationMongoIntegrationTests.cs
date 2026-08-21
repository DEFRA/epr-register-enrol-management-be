using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-351 regression: the queried sla-extend snapshot migration is the first
/// migration to actually call <see cref="IWorkItemPersistence.ReplaceAsync"/>
/// on a <em>current-version</em> seeded item (its siblings all skip once their
/// marker is satisfied). This exercises the migration against real Mongo — the
/// same BSON conventions and full-document round-trip the seeded E2E data goes
/// through — to prove it adds the transition WITHOUT dropping the operator
/// application payload (ORS / interim / BES / isNewSite) the detail page reads.
/// </summary>
public sealed class ReAccreditationSlaExtendQueryMigrationMongoIntegrationTests
    : IAsyncDisposable
{
    private static readonly DateTime Now = new(2026, 4, 27, 10, 0, 0, DateTimeKind.Utc);

    private readonly TestMongoDbClientFactory _clientFactory;
    private readonly string _databaseName;
    private readonly WorkItemPersistence _persistence;

    public ReAccreditationSlaExtendQueryMigrationMongoIntegrationTests(
        MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("reaccred-sla-migration");
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new WorkItemPersistence(_clientFactory, NullLoggerFactory.Instance);
    }

    public async ValueTask DisposeAsync() =>
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

    private static WorkItemTemplateSnapshot BuildV12Snapshot()
    {
        var snapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        return new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v12",
            States = snapshot.States,
            Transitions = snapshot
                .Transitions.Where(t =>
                    !(t.ActionId == "sla-extend" && t.FromStateId == "queried"))
                .ToList(),
        };
    }

    // A payload shaped like the RA-292 / full-payload E2E fixtures: four
    // overseas sites (one carrying BES evidence + an interim site + isNewSite),
    // plus top-level operator keys the WorkItem model does not map.
    private static BsonDocument BuildRichPayload() =>
        new()
        {
            ["applicationReference"] = "RA-292-FULL-001",
            ["operatorApplicationId"] = "app-full-001",
            ["organisationName"] = "Full Payload Verification Ltd",
            ["overseasSites"] = new BsonDocument
            {
                ["sites"] = new BsonArray
                {
                    new BsonDocument
                    {
                        ["siteName"] = "Site 0",
                        ["orsId"] = "ORS-2026-0000",
                        ["isNewSite"] = true,
                        ["interimSite"] = new BsonDocument
                        {
                            ["siteName"] = "Interim 0",
                            ["isNewSite"] = true,
                        },
                        ["besEvidence"] = new BsonDocument
                        {
                            ["files"] = new BsonArray
                            {
                                new BsonDocument
                                {
                                    ["fileId"] = "bes-001",
                                    ["filename"] = "bes-evidence.pdf",
                                },
                            },
                        },
                    },
                    new BsonDocument { ["siteName"] = "Site 1", ["isNewSite"] = false },
                    new BsonDocument { ["siteName"] = "Site 2" },
                    new BsonDocument { ["siteName"] = "Site 3", ["isNewSite"] = false },
                },
            },
        };

    [Fact]
    public async Task Migration_adds_transition_and_preserves_full_operator_payload()
    {
        var ct = TestContext.Current.CancellationToken;

        var item = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "queried",
            SubmittedAt = Now,
            LastModifiedAt = Now,
            SubmittedBy = "seed",
            TemplateVersion = "v12",
            TemplateSnapshot = BuildV12Snapshot(),
            Payload = BuildRichPayload(),
        };
        await _persistence.CreateAsync(item, ct);

        var migration = new ReAccreditationSlaExtendQuerySnapshotMigration(
            NullLogger<ReAccreditationSlaExtendQuerySnapshotMigration>.Instance);
        await migration.ApplyAsync(_persistence, ct);

        var reloaded = await _persistence.GetByIdAsync(item.Id, ct);
        Assert.NotNull(reloaded);

        // AC1/AC2: the queried sla-extend transition was added and the version bumped.
        Assert.Equal("v13", reloaded!.TemplateVersion);
        Assert.Contains(
            reloaded.TemplateSnapshot!.Transitions,
            t => t.ActionId == "sla-extend" && t.FromStateId == "queried");

        // The whole point: the operator application payload survives the
        // migration's GetById -> mutate-snapshot -> ReplaceAsync round-trip.
        var payload = reloaded.Payload;
        Assert.Equal("RA-292-FULL-001", payload["applicationReference"].AsString);
        Assert.Equal("app-full-001", payload["operatorApplicationId"].AsString);

        var sites = payload["overseasSites"]["sites"].AsBsonArray;
        Assert.Equal(4, sites.Count);

        var site0 = sites[0].AsBsonDocument;
        Assert.True(site0["isNewSite"].AsBoolean);
        Assert.Equal("Interim 0", site0["interimSite"]["siteName"].AsString);
        Assert.Equal(
            "bes-evidence.pdf",
            site0["besEvidence"]["files"][0]["filename"].AsString);
    }
}
