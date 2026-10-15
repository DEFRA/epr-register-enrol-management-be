using System.Net;
using EprRegisterEnrolManagementBe.Auth;
using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-000: the two e2e- fixtures, built and then reset through the real
/// startup path (TestOrganisationFixtureHostedService over real ephemeral
/// Mongo).
/// </summary>
public class ReAccreditationTestOrganisationFixturesTests
{
    private static readonly Guid s_legacyId = Guid.Parse(
        ReAccreditationTestOrganisationFixtures.LegacySnapshotId
    );
    private static readonly Guid s_preYearStartId = Guid.Parse(
        ReAccreditationTestOrganisationFixtures.PreYearStartId
    );

    private readonly MongoIntegrationFixture _fixture;

    public ReAccreditationTestOrganisationFixturesTests(MongoIntegrationFixture fixture) =>
        _fixture = fixture;

    // ------------------------------- unit -------------------------------

    /// <summary>
    /// The clock start is 14 November of the previous year at midnight UTC,
    /// so it is before 1 January of the current year on every day of the
    /// year, including either side of New Year.
    /// </summary>
    [Theory]
    [InlineData("2026-01-01T00:00:00Z", 2025)]
    [InlineData("2026-06-15T12:00:00Z", 2025)]
    [InlineData("2026-12-31T23:59:59Z", 2025)]
    [InlineData("2027-01-01T00:00:00Z", 2026)]
    public void The_pre_year_start_clock_starts_before_1_january_of_the_current_year(
        string now,
        int expectedYear
    )
    {
        var nowUtc = DateTimeOffset.Parse(now).UtcDateTime;

        var start = ReAccreditationTestOrganisationFixtures.PreYearStartClockStart(nowUtc);

        Assert.Equal(new DateTime(expectedYear, 11, 14, 0, 0, 0, DateTimeKind.Utc), start);
        Assert.True(start < new DateTime(nowUtc.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Both_fixtures_are_on_e2e_test_organisations_with_fixed_ids()
    {
        var documents = BuildFixtures(new FakeTimeProvider(DateTimeOffset.Parse("2026-10-15T09:00:00Z")));

        Assert.Equal(
            [ReAccreditationTestOrganisationFixtures.LegacySnapshotId, ReAccreditationTestOrganisationFixtures.PreYearStartId],
            documents.Select(d => d["_id"].AsString)
        );
        Assert.All(
            documents,
            d =>
                Assert.True(
                    TestOrganisations.IsTestOrganisation(
                        d["payload"]["operatorOrganisationId"].AsString
                    )
                )
        );
    }

    [Fact]
    public void The_legacy_fixture_carries_snapshot_elements_the_model_no_longer_declares()
    {
        var legacy = BuildFixtures(new FakeTimeProvider())[0];
        var snapshot = legacy["templateSnapshot"].AsBsonDocument;

        Assert.Equal("legacy", snapshot["retiredSnapshotField"].AsString);
        Assert.Equal("amber", snapshot["states"][0]["colour"].AsString);
        Assert.Equal(2, snapshot["transitions"][0]["requiredRoles"].AsBsonArray.Count);
        Assert.True(snapshot["tasksByState"]["submitted"][0]["mandatory"].AsBoolean);
    }

    // ---------------------------- integration ----------------------------

    /// <summary>
    /// RA-342: once reset into Mongo, the raw legacy document still lists on
    /// the worklist and opens by id over HTTP — the two paths the original
    /// crash broke.
    /// </summary>
    [Fact]
    public async Task The_legacy_fixture_lists_on_the_worklist_and_opens_by_id()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientIdDefaults.DefaultHeaderName, "frontend");

        var list = await client.GetAsync("/work-items?pageSize=100", ct);
        var byId = await client.GetAsync($"/work-items/{s_legacyId}", ct);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains(s_legacyId.ToString(), await list.Content.ReadAsStringAsync(ct));
        Assert.Equal(HttpStatusCode.OK, byId.StatusCode);
        Assert.Contains(
            ReAccreditationTestOrganisationFixtures.LegacySnapshotOrganisationName,
            await byId.Content.ReadAsStringAsync(ct)
        );
    }

    [Fact]
    public async Task The_pre_year_start_fixture_carries_the_current_snapshot_and_last_years_clock()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = NewFactory();
        var persistence = factory.Services.GetRequiredService<IWorkItemPersistence>();

        var item = await persistence.GetByIdAsync(s_preYearStartId, ct);

        Assert.NotNull(item);
        Assert.Equal("assessment-in-progress", item!.StateId);
        Assert.Null(item.AssignedToId);
        Assert.Equal(new ReAccreditationType().TemplateVersion, item.TemplateVersion);
        Assert.NotNull(item.TemplateSnapshot);
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        Assert.Equal(
            ReAccreditationTestOrganisationFixtures.PreYearStartClockStart(now),
            item.SlaClock!.StartedAt
        );
    }

    /// <summary>
    /// Each boot REPLACES the fixtures with their pristine documents, so
    /// whatever a journey run did to them (assignment, state, audit, version)
    /// is gone.
    /// </summary>
    [Fact]
    public async Task A_reset_restores_a_mutated_fixture()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = NewFactory();
        var persistence = factory.Services.GetRequiredService<IWorkItemPersistence>();
        var mutated = (await persistence.GetByIdAsync(s_preYearStartId, ct))!;
        mutated.StateId = "approved";
        mutated.AssignedToId = "someone-else";
        mutated.SlaClock!.StartedAt = DateTime.UtcNow;
        mutated.AuditLog.Add(new WorkItemAuditEntry { Action = "action-applied", ActionDisplayName = "Approve", CreatedBy = "e2e" });
        await persistence.ReplaceAsync(mutated, ct);

        var resetService = factory
            .Services.GetServices<IHostedService>()
            .OfType<TestOrganisationFixtureHostedService>()
            .Single();
        await resetService.StartAsync(ct);

        var restored = await persistence.GetByIdAsync(s_preYearStartId, ct);
        Assert.Equal("assessment-in-progress", restored!.StateId);
        Assert.Null(restored.AssignedToId);
        Assert.Empty(restored.AuditLog);
        Assert.Equal(0, restored.Version);
        Assert.True(restored.SlaClock!.StartedAt.Year < DateTime.UtcNow.Year);
    }

    private EphemeralMongoTestFactory NewFactory() =>
        new(
            _fixture,
            "e2e-fixtures",
            settings: new Dictionary<string, string?> { [TestOrganisations.EnabledKey] = "true" }
        );

    private static List<BsonDocument> BuildFixtures(TimeProvider time) =>
        new ReAccreditationTestOrganisationFixtures().Build(new ReAccreditationType(), time).ToList();
}
