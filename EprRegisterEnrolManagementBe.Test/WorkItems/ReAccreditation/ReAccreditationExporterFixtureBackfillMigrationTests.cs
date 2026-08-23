using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

public class ReAccreditationExporterFixtureBackfillMigrationTests
{
    private static readonly Guid s_fullPayloadId = WorkItemSeed.DeterministicId(
        ReAccreditationType.Id, ReAccreditationSeeder.FullPayloadVerificationSeedKey);

    private static readonly Guid s_orsInterimAuthorityId = WorkItemSeed.DeterministicId(
        ReAccreditationType.Id, ReAccreditationSeeder.OrsInterimAuthoritySeedKey);

    private static WorkItem BuildItem(Guid id, BsonDocument? payload = null) => new()
    {
        Id = id,
        TypeId = ReAccreditationType.Id,
        StateId = "submitted",
        Payload = payload ?? new BsonDocument { ["organisationName"] = "Full Payload Verification Ltd" }
    };

    private static ReAccreditationExporterFixtureBackfillMigration BuildSut(TimeProvider? clock = null) =>
        new(NullLogger<ReAccreditationExporterFixtureBackfillMigration>.Instance, clock);

    private static IWorkItemPersistence BuildPersistence(
        WorkItem? fullPayload = null, WorkItem? orsInterimAuthority = null)
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.GetByIdAsync(s_fullPayloadId, Arg.Any<CancellationToken>()).Returns(fullPayload);
        persistence.GetByIdAsync(s_orsInterimAuthorityId, Arg.Any<CancellationToken>())
            .Returns(orsInterimAuthority);
        return persistence;
    }

    [Fact]
    public async Task ApplyAsync_backfills_wasteProcessingType_on_both_known_fixtures()
    {
        var ct = TestContext.Current.CancellationToken;
        var fullPayload = BuildItem(s_fullPayloadId);
        var orsInterimAuthority = BuildItem(s_orsInterimAuthorityId);
        var persistence = BuildPersistence(fullPayload, orsInterimAuthority);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Equal("exporter", fullPayload.Payload["wasteProcessingType"].AsString);
        Assert.Equal("exporter", orsInterimAuthority.Payload["wasteProcessingType"].AsString);
    }

    [Fact]
    public async Task ApplyAsync_backfills_companyRegisterAddressPostcode_too()
    {
        var ct = TestContext.Current.CancellationToken;
        var fullPayload = BuildItem(s_fullPayloadId);
        var persistence = BuildPersistence(fullPayload);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.False(string.IsNullOrWhiteSpace(
            fullPayload.Payload["companyRegisterAddressPostcode"].AsString));
    }

    [Fact]
    public async Task ApplyAsync_appends_waste_processing_type_backfilled_audit_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var fullPayload = BuildItem(s_fullPayloadId);
        var persistence = BuildPersistence(fullPayload);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Contains(fullPayload.AuditLog, e =>
            e.Action == "waste-processing-type-backfilled" &&
            e.CreatedBy == "migration" &&
            e.Details["wasteProcessingType"] == "exporter");
    }

    [Fact]
    public async Task ApplyAsync_skips_a_fixture_that_already_has_both_fields()
    {
        var ct = TestContext.Current.CancellationToken;
        var fullPayload = BuildItem(s_fullPayloadId, new BsonDocument
        {
            ["organisationName"] = "Full Payload Verification Ltd",
            ["wasteProcessingType"] = "exporter",
            ["companyRegisterAddressPostcode"] = "G2 1AL"
        });
        var persistence = BuildPersistence(fullPayload);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task ApplyAsync_backfills_only_the_postcode_when_wasteProcessingType_is_already_set()
    {
        // Regression: the two fields used to be gated together, so a fixture
        // that already had wasteProcessingType but was missing the postcode
        // (e.g. a partially-applied prior run) was skipped entirely and the
        // postcode was never backfilled.
        var ct = TestContext.Current.CancellationToken;
        var fullPayload = BuildItem(s_fullPayloadId, new BsonDocument
        {
            ["organisationName"] = "Full Payload Verification Ltd",
            ["wasteProcessingType"] = "exporter"
        });
        var persistence = BuildPersistence(fullPayload);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.Received(1).ReplaceAsync(fullPayload, Arg.Any<CancellationToken>());
        Assert.Equal("G2 1AL", fullPayload.Payload["companyRegisterAddressPostcode"].AsString);
        var entry = fullPayload.AuditLog.Single(e => e.Action == "waste-processing-type-backfilled");
        Assert.False(entry.Details.ContainsKey("wasteProcessingType"));
        Assert.Equal("G2 1AL", entry.Details["companyRegisterAddressPostcode"]);
    }

    [Fact]
    public async Task ApplyAsync_backfills_only_wasteProcessingType_when_postcode_is_already_set()
    {
        var ct = TestContext.Current.CancellationToken;
        var fullPayload = BuildItem(s_fullPayloadId, new BsonDocument
        {
            ["organisationName"] = "Full Payload Verification Ltd",
            ["companyRegisterAddressPostcode"] = "G2 1AL"
        });
        var persistence = BuildPersistence(fullPayload);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.Received(1).ReplaceAsync(fullPayload, Arg.Any<CancellationToken>());
        Assert.Equal("exporter", fullPayload.Payload["wasteProcessingType"].AsString);
        var entry = fullPayload.AuditLog.Single(e => e.Action == "waste-processing-type-backfilled");
        Assert.False(entry.Details.ContainsKey("companyRegisterAddressPostcode"));
        Assert.Equal("exporter", entry.Details["wasteProcessingType"]);
    }

    [Fact]
    public async Task ApplyAsync_never_touches_any_id_other_than_the_two_known_fixtures()
    {
        // The whole point of scoping by id rather than a payload predicate:
        // real pre-RA-314 data with overseasSites but no wasteProcessingType
        // must be left alone (it correctly falls back to "Reprocessor").
        var ct = TestContext.Current.CancellationToken;
        var persistence = BuildPersistence();

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        await persistence.DidNotReceiveWithAnyArgs().QueryAsync(default!, ct);
    }

    [Fact]
    public async Task ApplyAsync_skips_a_fixture_id_that_does_not_exist_yet()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistence = BuildPersistence();

        // Must not throw when GetByIdAsync returns null for both ids.
        await BuildSut().ApplyAsync(persistence, ct);
    }

    [Fact]
    public async Task ApplyAsync_uses_injected_time_for_audit_entry_created_at()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        var frozen = new DateTimeOffset(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);
        clock.SetUtcNow(frozen);

        var fullPayload = BuildItem(s_fullPayloadId);
        var persistence = BuildPersistence(fullPayload);

        await BuildSut(clock).ApplyAsync(persistence, ct);

        var entry = fullPayload.AuditLog.Single(e => e.Action == "waste-processing-type-backfilled");
        Assert.Equal(frozen.UtcDateTime, entry.CreatedAt);
    }

    [Fact]
    public async Task ApplyAsync_swallows_concurrency_exception_and_continues()
    {
        var ct = TestContext.Current.CancellationToken;
        var fullPayload = BuildItem(s_fullPayloadId);
        var orsInterimAuthority = BuildItem(s_orsInterimAuthorityId);
        var persistence = BuildPersistence(fullPayload, orsInterimAuthority);
        persistence.ReplaceAsync(fullPayload, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(
                new WorkItemConcurrencyException(fullPayload.Id, expectedVersion: 0)));

        await BuildSut().ApplyAsync(persistence, ct);

        // The other fixture is unaffected by the first one's conflict.
        Assert.Equal("exporter", orsInterimAuthority.Payload["wasteProcessingType"].AsString);
    }
}
