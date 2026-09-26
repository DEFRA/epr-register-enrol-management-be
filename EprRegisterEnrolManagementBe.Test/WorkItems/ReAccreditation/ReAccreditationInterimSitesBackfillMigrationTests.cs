using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

// RA-603. An overseas reprocessing site used to hold at most one interim site, in a singular
// `interimSite` field. It can now hold many, in `interimSites`. This copies the old value into the
// new list so a work item seeded or submitted before RA-603 reads the same as one submitted after,
// without the regulator's view having to understand both shapes.
//
// The singular field is deliberately left in place. It is the mirror every consumer that has not
// moved over still reads, and removing it is a separate piece of work.
public class ReAccreditationInterimSitesBackfillMigrationTests
{
    private static BsonDocument InterimSite(int siteId, string siteName, bool isNewSite = false) =>
        new()
        {
            ["siteId"] = siteId,
            ["siteNumber"] = $"INT-{siteId:D3}",
            ["isNewSite"] = isNewSite,
            ["country"] = "Belgium",
            ["siteName"] = siteName,
            ["operationCodes"] = new BsonArray { "R12" },
        };

    private static BsonDocument PayloadWithSites(params BsonDocument[] sites) =>
        new() { ["overseasSites"] = new BsonDocument { ["sites"] = new BsonArray(sites) } };

    private static BsonDocument SiteWithSingular(BsonDocument interimSite) =>
        new()
        {
            ["siteId"] = 1,
            ["siteName"] = "Antwerp ORS",
            ["interimSite"] = interimSite,
        };

    private static WorkItem BuildItem(BsonDocument? payload = null) =>
        new()
        {
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            Payload = payload ?? PayloadWithSites(SiteWithSingular(InterimSite(11, "Antwerp"))),
        };

    private static WorkItemPage SinglePage(params WorkItem[] items) =>
        new(items, items.Length, 1, WorkItemQuery.MaxPageSize);

    private static ReAccreditationInterimSitesBackfillMigration BuildSut(
        TimeProvider? clock = null
    ) => new(NullLogger<ReAccreditationInterimSitesBackfillMigration>.Instance, clock);

    private static IWorkItemPersistence PersistenceFor(WorkItem item, CancellationToken ct)
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);
        return persistence;
    }

    private static BsonArray InterimSitesOf(WorkItem item, int siteIndex = 0) =>
        item.Payload["overseasSites"]["sites"][siteIndex]["interimSites"].AsBsonArray;

    [Fact]
    public async Task ApplyAsync_copies_the_singular_interim_site_into_the_new_list()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();

        await BuildSut().ApplyAsync(PersistenceFor(item, ct), ct);

        var interimSites = InterimSitesOf(item);
        Assert.Single(interimSites);
        Assert.Equal("Antwerp", interimSites[0]["siteName"].AsString);
    }

    // The mirror stays. Anything still reading it keeps working until it is retired separately.
    [Fact]
    public async Task ApplyAsync_leaves_the_singular_field_in_place()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();

        await BuildSut().ApplyAsync(PersistenceFor(item, ct), ct);

        var site = item.Payload["overseasSites"]["sites"][0].AsBsonDocument;
        Assert.True(site.Contains("interimSite"));
        Assert.Equal("Antwerp", site["interimSite"]["siteName"].AsString);
    }

    // isNewSite drives the regulator's "new" badge. A migration is not the place to re-decide it.
    [Fact]
    public async Task ApplyAsync_carries_isNewSite_over_verbatim()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(
            PayloadWithSites(SiteWithSingular(InterimSite(11, "Antwerp", isNewSite: true)))
        );

        await BuildSut().ApplyAsync(PersistenceFor(item, ct), ct);

        Assert.True(InterimSitesOf(item)[0]["isNewSite"].AsBoolean);
    }

    // createdAt exists for auditability. These records genuinely predate it, so it stays absent
    // rather than being stamped with a time nobody can vouch for.
    [Fact]
    public async Task ApplyAsync_does_not_invent_a_createdAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();

        await BuildSut().ApplyAsync(PersistenceFor(item, ct), ct);

        Assert.False(InterimSitesOf(item)[0].AsBsonDocument.Contains("createdAt"));
    }

    [Fact]
    public async Task ApplyAsync_migrates_every_overseas_site_on_the_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var second = new BsonDocument
        {
            ["siteId"] = 2,
            ["siteName"] = "Rotterdam ORS",
            ["interimSite"] = InterimSite(12, "Rotterdam"),
        };
        var item = BuildItem(
            PayloadWithSites(SiteWithSingular(InterimSite(11, "Antwerp")), second)
        );

        await BuildSut().ApplyAsync(PersistenceFor(item, ct), ct);

        Assert.Equal("Antwerp", InterimSitesOf(item, 0)[0]["siteName"].AsString);
        Assert.Equal("Rotterdam", InterimSitesOf(item, 1)[0]["siteName"].AsString);
    }

    [Fact]
    public async Task ApplyAsync_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = PersistenceFor(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);
        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Single(InterimSitesOf(item));
    }

    // A work item already written in the new shape must not have its list overwritten by a stale
    // mirror - the list is the authority, not the mirror.
    [Fact]
    public async Task ApplyAsync_does_not_touch_an_item_that_already_has_a_populated_list()
    {
        var ct = TestContext.Current.CancellationToken;
        var site = SiteWithSingular(InterimSite(11, "Stale Mirror"));
        site["interimSites"] = new BsonArray
        {
            InterimSite(11, "Authoritative"),
            InterimSite(12, "Second"),
        };
        var item = BuildItem(PayloadWithSites(site));
        var persistence = PersistenceFor(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Equal(2, InterimSitesOf(item).Count);
        Assert.Equal("Authoritative", InterimSitesOf(item)[0]["siteName"].AsString);
        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    // An ORS that never had an interim site gets an empty list, so consumers can read one shape
    // rather than branching on whether the field exists.
    [Fact]
    public async Task ApplyAsync_gives_a_site_with_no_interim_site_an_empty_list()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = new BsonDocument { ["siteId"] = 1, ["siteName"] = "Bare ORS" };
        var item = BuildItem(PayloadWithSites(bare));

        await BuildSut().ApplyAsync(PersistenceFor(item, ct), ct);

        Assert.Empty(InterimSitesOf(item));
    }

    [Fact]
    public async Task ApplyAsync_records_an_audit_entry_when_it_changes_an_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero));

        await BuildSut(clock).ApplyAsync(PersistenceFor(item, ct), ct);

        var entry = Assert.Single(item.AuditLog);
        Assert.Equal("interim-sites-backfilled", entry.Action);
        Assert.Equal("migration", entry.CreatedBy);
    }

    [Fact]
    public async Task ApplyAsync_ignores_an_item_with_no_overseas_sites_section()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(new BsonDocument { ["material"] = "plastic" });
        var persistence = PersistenceFor(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task ApplyAsync_skips_an_item_whose_full_document_has_disappeared()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns((WorkItem?)null);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }
}
