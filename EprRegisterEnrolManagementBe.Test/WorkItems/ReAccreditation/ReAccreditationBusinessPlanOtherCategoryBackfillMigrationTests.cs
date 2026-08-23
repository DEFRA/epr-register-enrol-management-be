using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

public class ReAccreditationBusinessPlanOtherCategoryBackfillMigrationTests
{
    private static readonly Guid s_fullPayloadId = WorkItemSeed.DeterministicId(
        ReAccreditationType.Id, ReAccreditationSeeder.FullPayloadVerificationSeedKey);

    private static BsonDocument BuildSixCategoryBusinessPlan() => new()
    {
        ["newInfrastructurePercent"] = 20,
        ["priceSupportPercent"] = 15,
        ["businessCollectionsPercent"] = 25,
        ["communicationsPercent"] = 10,
        ["newMarketsPercent"] = 20,
        ["newUsesPercent"] = 10,
        ["newInfrastructureDetail"] = "New sorting line investment",
        ["priceSupportDetail"] = "Subsidised collection scheme",
        ["businessCollectionsDetail"] = "Kerbside collection expansion",
        ["communicationsDetail"] = "Customer awareness campaign",
        ["newMarketsDetail"] = "New export contracts secured",
        ["newUsesDetail"] = "Recycled content packaging trial",
    };

    private static WorkItem BuildItem(BsonDocument? businessPlan) => new()
    {
        Id = s_fullPayloadId,
        TypeId = ReAccreditationType.Id,
        StateId = "submitted",
        Payload = businessPlan is null
            ? new BsonDocument { ["organisationName"] = "Full Payload Verification Ltd" }
            : new BsonDocument
            {
                ["organisationName"] = "Full Payload Verification Ltd",
                ["businessPlan"] = businessPlan
            }
    };

    private static ReAccreditationBusinessPlanOtherCategoryBackfillMigration BuildSut(
        TimeProvider? clock = null) =>
        new(NullLogger<ReAccreditationBusinessPlanOtherCategoryBackfillMigration>.Instance, clock);

    private static IWorkItemPersistence BuildPersistence(WorkItem? fullPayload) =>
        BuildPersistenceSubstitute(fullPayload);

    private static IWorkItemPersistence BuildPersistenceSubstitute(WorkItem? fullPayload)
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.GetByIdAsync(s_fullPayloadId, Arg.Any<CancellationToken>()).Returns(fullPayload);
        return persistence;
    }

    [Fact]
    public async Task ApplyAsync_backfills_otherPercent_and_otherDetail_onto_a_six_category_plan()
    {
        var ct = TestContext.Current.CancellationToken;
        var fullPayload = BuildItem(BuildSixCategoryBusinessPlan());
        var persistence = BuildPersistence(fullPayload);

        await BuildSut().ApplyAsync(persistence, ct);

        var businessPlan = fullPayload.Payload["businessPlan"].AsBsonDocument;
        Assert.Equal(10, businessPlan["otherPercent"].AsInt32);
        Assert.Equal(
            "Contribution to sector-wide research and development initiatives",
            businessPlan["otherDetail"].AsString);
    }

    [Fact]
    public async Task ApplyAsync_rebalances_businessCollections_and_newMarkets_so_the_total_stays_100()
    {
        var ct = TestContext.Current.CancellationToken;
        var fullPayload = BuildItem(BuildSixCategoryBusinessPlan());
        var persistence = BuildPersistence(fullPayload);

        await BuildSut().ApplyAsync(persistence, ct);

        var businessPlan = fullPayload.Payload["businessPlan"].AsBsonDocument;
        Assert.Equal(20, businessPlan["businessCollectionsPercent"].AsInt32);
        Assert.Equal(15, businessPlan["newMarketsPercent"].AsInt32);

        var total = businessPlan["newInfrastructurePercent"].AsInt32
            + businessPlan["priceSupportPercent"].AsInt32
            + businessPlan["businessCollectionsPercent"].AsInt32
            + businessPlan["communicationsPercent"].AsInt32
            + businessPlan["newMarketsPercent"].AsInt32
            + businessPlan["newUsesPercent"].AsInt32
            + businessPlan["otherPercent"].AsInt32;
        Assert.Equal(100, total);
    }

    [Fact]
    public async Task ApplyAsync_appends_business_plan_other_category_backfilled_audit_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var fullPayload = BuildItem(BuildSixCategoryBusinessPlan());
        var persistence = BuildPersistence(fullPayload);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Contains(fullPayload.AuditLog, e =>
            e.Action == "business-plan-other-category-backfilled" &&
            e.CreatedBy == "migration" &&
            e.Details["otherPercent"] == "10");
    }

    [Fact]
    public async Task ApplyAsync_skips_a_plan_that_already_has_otherPercent()
    {
        var ct = TestContext.Current.CancellationToken;
        var businessPlan = BuildSixCategoryBusinessPlan();
        businessPlan["otherPercent"] = 10;
        businessPlan["otherDetail"] = "Already backfilled";
        var fullPayload = BuildItem(businessPlan);
        var persistence = BuildPersistence(fullPayload);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
        Assert.Equal("Already backfilled", businessPlan["otherDetail"].AsString);
    }

    [Fact]
    public async Task ApplyAsync_skips_a_fixture_id_that_does_not_exist_yet()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistence = BuildPersistence(null);

        // Must not throw when GetByIdAsync returns null.
        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }

    [Fact]
    public async Task ApplyAsync_skips_a_fixture_with_no_businessPlan_at_all()
    {
        var ct = TestContext.Current.CancellationToken;
        var fullPayload = BuildItem(null);
        var persistence = BuildPersistence(fullPayload);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }

    [Fact]
    public async Task ApplyAsync_uses_injected_time_for_audit_entry_created_at()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        var frozen = new DateTimeOffset(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);
        clock.SetUtcNow(frozen);

        var fullPayload = BuildItem(BuildSixCategoryBusinessPlan());
        var persistence = BuildPersistence(fullPayload);

        await BuildSut(clock).ApplyAsync(persistence, ct);

        var entry = fullPayload.AuditLog.Single(
            e => e.Action == "business-plan-other-category-backfilled");
        Assert.Equal(frozen.UtcDateTime, entry.CreatedAt);
    }

    [Fact]
    public async Task ApplyAsync_swallows_concurrency_exception()
    {
        var ct = TestContext.Current.CancellationToken;
        var fullPayload = BuildItem(BuildSixCategoryBusinessPlan());
        var persistence = BuildPersistence(fullPayload);
        persistence.ReplaceAsync(fullPayload, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(
                new WorkItemConcurrencyException(fullPayload.Id, expectedVersion: 0)));

        // Must not throw.
        await BuildSut().ApplyAsync(persistence, ct);
    }
}
