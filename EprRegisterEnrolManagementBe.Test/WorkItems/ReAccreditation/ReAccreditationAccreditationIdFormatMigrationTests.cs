using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// epr-accreditation-id-format AC02: backfill of the old
/// <c>ACC-{Year}-{Material}-{ULID8}</c> accreditation id shape to the new
/// fixed-width format. Gated like
/// <see cref="ReAccreditationIsNewSiteCorrectionMigration"/> — an already
/// issued id may already have been quoted externally — so every gate is
/// tested for its refusal, not just the happy path.
/// </summary>
public class ReAccreditationAccreditationIdFormatMigrationTests
{
    private static readonly DateTimeOffset s_now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    private static WorkItem BuildItem(string? accreditationId = "ACC-2025-P-DEADBEEF") =>
        new()
        {
            TypeId = ReAccreditationType.Id,
            StateId = "approved",
            Payload = new BsonDocument
            {
                ["organisationName"] = "Acme Ltd",
                ["material"] = "plastic",
                ["accreditationYear"] = 2025,
                ["accreditationId"] = accreditationId is null ? BsonNull.Value : accreditationId
            }
        };

    private static IConfiguration Config(bool? enabled = true, bool? apply = true)
    {
        var values = new Dictionary<string, string?>();
        if (enabled is not null)
        {
            values[ReAccreditationAccreditationIdFormatMigration.EnabledConfigKey] = enabled.Value.ToString();
        }

        if (apply is not null)
        {
            values[ReAccreditationAccreditationIdFormatMigration.ApplyConfigKey] = apply.Value.ToString();
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IAccreditationIdGenerator NewFormatGenerator(string returns = "A25ER1234561BBPL")
    {
        var generator = Substitute.For<IAccreditationIdGenerator>();
        generator
            .GenerateAsync(Arg.Any<BsonDocument>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(returns);
        return generator;
    }

    private static IServiceProvider ServiceProviderWith(IAccreditationIdGenerator generator)
    {
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IAccreditationIdGenerator)).Returns(generator);
        return provider;
    }

    private static ReAccreditationAccreditationIdFormatMigration BuildSut(
        IConfiguration configuration, IAccreditationIdGenerator? generator = null) =>
        new(
            configuration,
            ServiceProviderWith(generator ?? NewFormatGenerator()),
            NullLogger<ReAccreditationAccreditationIdFormatMigration>.Instance,
            new FakeTimeProvider(s_now));

    private static IWorkItemPersistence PersistenceWith(params WorkItem[] items)
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence
            .QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(items, items.Length, 1, WorkItemQuery.MaxPageSize));
        foreach (var item in items)
        {
            persistence.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        }

        return persistence;
    }

    // ── Gate 1: off by default ───────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_does_nothing_when_the_feature_is_not_enabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = PersistenceWith(item);

        await BuildSut(Config(enabled: false)).ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().QueryAsync(default!, default);
        Assert.Equal("ACC-2025-P-DEADBEEF", item.Payload["accreditationId"].AsString);
    }

    [Fact]
    public async Task ApplyAsync_does_nothing_when_the_enabled_flag_is_absent()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = PersistenceWith(item);

        await BuildSut(Config(enabled: null)).ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().QueryAsync(default!, default);
    }

    // ── Gate 2: dry run unless apply is explicitly set ───────────────────────

    [Fact]
    public async Task ApplyAsync_writes_nothing_in_dry_run_mode()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = PersistenceWith(item);

        await BuildSut(Config(apply: false)).ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
        Assert.Equal("ACC-2025-P-DEADBEEF", item.Payload["accreditationId"].AsString);
        Assert.Empty(item.AuditLog);
    }

    [Fact]
    public async Task ApplyAsync_replaces_the_old_format_id_when_applied()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = PersistenceWith(item);

        await BuildSut(Config(), NewFormatGenerator("A25ER1234561BBPL")).ApplyAsync(persistence, ct);

        Assert.Equal("A25ER1234561BBPL", item.Payload["accreditationId"].AsString);
        await persistence.Received(1).ReplaceAsync(item, ct);
    }

    [Fact]
    public async Task ApplyAsync_appends_an_audit_entry_recording_old_and_new_ids()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = PersistenceWith(item);

        await BuildSut(Config(), NewFormatGenerator("A25ER1234561BBPL")).ApplyAsync(persistence, ct);

        Assert.Contains(item.AuditLog, e =>
            e.Action == ReAccreditationAccreditationIdFormatMigration.AuditAction &&
            e.CreatedBy == "migration" &&
            e.Details["previousAccreditationId"] == "ACC-2025-P-DEADBEEF" &&
            e.Details["accreditationId"] == "A25ER1234561BBPL");
    }

    [Fact]
    public async Task ApplyAsync_uses_injected_time_for_the_audit_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = PersistenceWith(item);

        await BuildSut(Config()).ApplyAsync(persistence, ct);

        var entry = item.AuditLog.Single(e => e.Action == ReAccreditationAccreditationIdFormatMigration.AuditAction);
        Assert.Equal(s_now.UtcDateTime, entry.CreatedAt);
    }

    // ── Idempotency / skip conditions ────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_skips_items_already_in_the_new_16_character_format()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(accreditationId: "A25ER1234561BBPL");
        var persistence = PersistenceWith(item);

        await BuildSut(Config()).ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }

    [Fact]
    public async Task ApplyAsync_skips_items_with_no_accreditation_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(accreditationId: null);
        var persistence = PersistenceWith(item);

        await BuildSut(Config()).ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }

    [Fact]
    public async Task ApplyAsync_swallows_concurrency_exception_and_continues()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = PersistenceWith(item);
        persistence.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new WorkItemConcurrencyException(item.Id, expectedVersion: 0)));

        await BuildSut(Config()).ApplyAsync(persistence, ct);
    }

    [Fact]
    public async Task ApplyAsync_only_queries_approved_re_accreditation_work_items()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct)
            .Returns(new WorkItemPage([], 0, 1, WorkItemQuery.MaxPageSize));

        await BuildSut(Config()).ApplyAsync(persistence, ct);

        await persistence.Received(1).QueryAsync(
            Arg.Is<WorkItemQuery>(q =>
                q.TypeIds != null && q.TypeIds.Contains(ReAccreditationType.Id) &&
                q.StateIds != null && q.StateIds.Contains("approved") &&
                q.IncludeArchived),
            ct);
    }
}
