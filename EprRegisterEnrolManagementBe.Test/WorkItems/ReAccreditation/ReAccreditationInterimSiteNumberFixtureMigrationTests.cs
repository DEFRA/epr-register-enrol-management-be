using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

public class ReAccreditationInterimSiteNumberFixtureMigrationTests
{
    private static readonly Guid s_fixtureId = WorkItemSeed.DeterministicId(
        ReAccreditationType.Id,
        ReAccreditationSeeder.OrsInterimAuthoritySeedKey
    );

    private static BsonDocument Interim(int siteId, string siteNumber) =>
        new() { ["siteId"] = siteId, ["siteNumber"] = siteNumber };

    // The fixture as an environment seeded before RA-603 holds it: the mirror only, or (once the
    // interim-sites backfill has run) the mirror plus a list copied from it.
    private static WorkItem BuildFixture(bool withList) =>
        new()
        {
            Id = s_fixtureId,
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            Payload = new BsonDocument
            {
                ["overseasSites"] = new BsonDocument
                {
                    ["sites"] = new BsonArray
                    {
                        Site(11, "INT-001", withList),
                        Site(21, "INT-002", withList),
                    },
                },
            },
        };

    private static BsonDocument Site(int interimId, string siteNumber, bool withList)
    {
        var site = new BsonDocument { ["interimSite"] = Interim(interimId, siteNumber) };
        if (withList)
            site["interimSites"] = new BsonArray { Interim(interimId, siteNumber) };
        return site;
    }

    private static IEnumerable<string> AllNumbers(WorkItem item) =>
        item.Payload["overseasSites"]["sites"]
            .AsBsonArray.Select(s => s.AsBsonDocument)
            .SelectMany(s =>
                new[] { s["interimSite"] }.Concat(
                    s.Contains("interimSites") ? s["interimSites"].AsBsonArray : []
                )
            )
            .Select(i => i["siteNumber"].AsString);

    private static ReAccreditationInterimSiteNumberFixtureMigration BuildSut() =>
        new(NullLogger<ReAccreditationInterimSiteNumberFixtureMigration>.Instance);

    private static IWorkItemPersistence BuildPersistence(WorkItem? fixture)
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.GetByIdAsync(s_fixtureId, Arg.Any<CancellationToken>()).Returns(fixture);
        return persistence;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_renumbers_the_mirror_and_the_list(bool withList)
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = BuildFixture(withList);
        var persistence = BuildPersistence(fixture);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.All(AllNumbers(fixture), n => Assert.Matches("^00[12]$", n));
        Assert.Equal(["001", "002"], AllNumbers(fixture).Distinct().Order());
        await persistence.Received(1).ReplaceAsync(fixture, Arg.Any<CancellationToken>());
        Assert.Contains(
            fixture.AuditLog,
            e => e.Action == "interim-site-numbers-renumbered" && e.CreatedBy == "migration"
        );
    }

    [Fact]
    public async Task ApplyAsync_is_a_no_op_on_a_fixture_already_renumbered()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = BuildFixture(withList: true);
        var persistence = BuildPersistence(fixture);
        await BuildSut().ApplyAsync(persistence, ct);
        persistence.ClearReceivedCalls();

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task ApplyAsync_leaves_any_other_number_alone()
    {
        // Only the exact old seeded values are rewritten; anything else on the document stays.
        var ct = TestContext.Current.CancellationToken;
        var fixture = BuildFixture(withList: false);
        fixture.Payload["overseasSites"]["sites"][1]["interimSite"]["siteNumber"] = "INT-003";
        var persistence = BuildPersistence(fixture);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Equal(["001", "INT-003"], AllNumbers(fixture));
    }

    [Fact]
    public async Task ApplyAsync_only_reads_the_one_fixture_id()
    {
        // Never a collection-wide query: real operator numbers are out of reach by construction.
        var ct = TestContext.Current.CancellationToken;
        var persistence = BuildPersistence(null);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.Received(1).GetByIdAsync(s_fixtureId, Arg.Any<CancellationToken>());
        await persistence.DidNotReceiveWithAnyArgs().QueryAsync(default!, ct);
        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task ApplyAsync_swallows_a_concurrency_conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = BuildFixture(withList: false);
        var persistence = BuildPersistence(fixture);
        persistence
            .ReplaceAsync(fixture, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException(new WorkItemConcurrencyException(fixture.Id, expectedVersion: 0))
            );

        await BuildSut().ApplyAsync(persistence, ct);
    }
}
