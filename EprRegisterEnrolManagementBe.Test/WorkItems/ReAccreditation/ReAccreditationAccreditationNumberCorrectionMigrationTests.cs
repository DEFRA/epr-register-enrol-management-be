using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-691: the accreditation number correction, run against real (ephemeral)
/// Mongo through the real <see cref="WorkItemPersistence"/> so the atomic
/// filtered update and "nothing else changed" are proved on the wire.
/// </summary>
public sealed class ReAccreditationAccreditationNumberCorrectionMigrationTests : IAsyncDisposable
{
    private const string InfinityIncorrect = "A27ER5005270322PL";
    private const string InfinityCorrected = "A27ER5005270246PL";
    private const string PlasgranIncorrect = "A27ER5001050321PL";
    private const string PlasgranCorrected = "A27ER5001050197PL";

    private static readonly DateTimeOffset s_now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _clientFactory;
    private readonly WorkItemPersistence _persistence;
    private readonly ListLogger<ReAccreditationAccreditationNumberCorrectionMigration> _logger = new();

    public ReAccreditationAccreditationNumberCorrectionMigrationTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("ra691");
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new WorkItemPersistence(_clientFactory, NullLoggerFactory.Instance);
    }

    public async ValueTask DisposeAsync() =>
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

    // ------------------------------- helpers -------------------------------

    private async Task<WorkItem> SeedAsync(string accreditationId, string organisationName = "Org")
    {
        var item = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = ReAccreditationType.ApprovedStateId,
            SubmittedAt = s_now.UtcDateTime.AddDays(-60),
            LastModifiedAt = s_now.UtcDateTime.AddDays(-10),
            SubmittedBy = "operator",
        };
        item.ReplacePayload(
            new BsonDocument
            {
                ["organisationName"] = organisationName,
                ["accreditationId"] = accreditationId,
            }
        );
        item.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = "accreditation-issued",
                ActionDisplayName = "Accreditation issued",
                CreatedAt = s_now.UtcDateTime.AddDays(-10),
                Details = new Dictionary<string, string?> { ["accreditationId"] = accreditationId },
            }
        );
        await _persistence.CreateAsync(item, TestContext.Current.CancellationToken);
        return item;
    }

    private async Task<(WorkItem Infinity, WorkItem Plasgran)> SeedBothIncorrectAsync() =>
        (
            await SeedAsync(InfinityIncorrect, "Infinity Plastic Solutions Ltd"),
            await SeedAsync(PlasgranIncorrect, "PLASGRAN LIMITED")
        );

    private ReAccreditationAccreditationNumberCorrectionMigration BuildSut(
        bool enabled = true,
        string? confirmedBy = "a.checker",
        bool apply = true
    )
    {
        var settings = new Dictionary<string, string?>
        {
            [ReAccreditationAccreditationNumberCorrectionMigration.SpotCheckConfirmedByConfigKey] = confirmedBy,
        };
        if (enabled)
        {
            settings[ReAccreditationAccreditationNumberCorrectionMigration.EnabledConfigKey] = "true";
        }
        if (apply)
        {
            settings[ReAccreditationAccreditationNumberCorrectionMigration.ApplyConfigKey] = "true";
        }

        return new(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            _logger,
            new FakeTimeProvider(s_now)
        );
    }

    private Task RunAsync(ReAccreditationAccreditationNumberCorrectionMigration sut) =>
        sut.ApplyAsync(_persistence, TestContext.Current.CancellationToken);

    private async Task<WorkItem> ReloadAsync(WorkItem item) =>
        (await _persistence.GetByIdAsync(item.Id, TestContext.Current.CancellationToken))!;

    private async Task AssertUntouchedAsync(WorkItem seeded)
    {
        var reloaded = await ReloadAsync(seeded);
        Assert.Equal(seeded.ToBsonDocument(), reloaded.ToBsonDocument());
    }

    // ------------------------------- gates -------------------------------

    [Fact]
    public async Task Disabled_by_default_writes_nothing()
    {
        var (infinity, plasgran) = await SeedBothIncorrectAsync();

        await RunAsync(BuildSut(enabled: false));

        await AssertUntouchedAsync(infinity);
        await AssertUntouchedAsync(plasgran);
        Assert.Empty(_logger.Records);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Apply_without_a_spot_check_logs_an_error_and_runs_as_a_dry_run(string? confirmedBy)
    {
        var (infinity, plasgran) = await SeedBothIncorrectAsync();

        await RunAsync(BuildSut(confirmedBy: confirmedBy));

        await AssertUntouchedAsync(infinity);
        await AssertUntouchedAsync(plasgran);
        var error = Assert.Single(_logger.Records, r => r.Level == LogLevel.Error);
        Assert.Contains(
            ReAccreditationAccreditationNumberCorrectionMigration.SpotCheckConfirmedByConfigKey,
            error.Message
        );
        Assert.Equal(
            2,
            _logger.Records.Count(r => r.Message.Contains("DRY RUN would correct", StringComparison.Ordinal))
        );
    }

    [Fact]
    public async Task Dry_run_does_not_need_a_spot_check()
    {
        var (infinity, plasgran) = await SeedBothIncorrectAsync();

        await RunAsync(BuildSut(confirmedBy: null, apply: false));

        await AssertUntouchedAsync(infinity);
        await AssertUntouchedAsync(plasgran);
        Assert.DoesNotContain(_logger.Records, r => r.Level == LogLevel.Error);
        Assert.Equal(
            2,
            _logger.Records.Count(r => r.Message.Contains("DRY RUN would correct", StringComparison.Ordinal))
        );
    }

    // AC1
    [Fact]
    public async Task Dry_run_logs_exactly_the_two_changes_and_writes_nothing()
    {
        var (infinity, plasgran) = await SeedBothIncorrectAsync();

        await RunAsync(BuildSut(apply: false));

        await AssertUntouchedAsync(infinity);
        await AssertUntouchedAsync(plasgran);
        var wouldCorrect = _logger
            .Records.Where(r => r.Message.Contains("DRY RUN would correct", StringComparison.Ordinal))
            .Select(r => r.Message)
            .ToList();
        Assert.Equal(2, wouldCorrect.Count);
        Assert.Contains(
            wouldCorrect,
            m => m.EndsWith(
                $"Infinity Plastic Solutions Ltd (500527) work item {infinity.Id}: {InfinityIncorrect} → {InfinityCorrected}",
                StringComparison.Ordinal
            )
        );
        Assert.Contains(
            wouldCorrect,
            m => m.EndsWith(
                $"PLASGRAN LIMITED (500105) work item {plasgran.Id}: {PlasgranIncorrect} → {PlasgranCorrected}",
                StringComparison.Ordinal
            )
        );
    }

    // ------------------------------- apply -------------------------------

    // AC2 + AC3
    [Fact]
    public async Task Apply_corrects_both_numbers_and_appends_one_audit_entry_each_without_a_state_change()
    {
        var (infinity, plasgran) = await SeedBothIncorrectAsync();

        await RunAsync(BuildSut());

        foreach (var (seeded, from, to) in new[]
        {
            (infinity, InfinityIncorrect, InfinityCorrected),
            (plasgran, PlasgranIncorrect, PlasgranCorrected),
        })
        {
            var reloaded = await ReloadAsync(seeded);
            Assert.Equal(to, reloaded.Payload["accreditationId"].AsString);
            Assert.Equal(ReAccreditationType.ApprovedStateId, reloaded.StateId);
            Assert.Equal(seeded.LastModifiedAt, reloaded.LastModifiedAt);
            Assert.Equal(seeded.Version + 1, reloaded.Version);

            // The earlier entry is history and keeps the old number.
            Assert.Equal(from, reloaded.AuditLog[0].Details["accreditationId"]);

            var entry = Assert.Single(reloaded.AuditLog, e => e.Action == "accreditation-number-corrected");
            Assert.Equal("Accreditation number corrected", entry.ActionDisplayName);
            Assert.Equal(
                new Dictionary<string, string?> { ["from"] = from, ["to"] = to, ["reason"] = "RA-691" },
                entry.Details
            );
            Assert.Equal("migration", entry.CreatedBy);
            Assert.Equal("Migration: RA-691 accreditation number correction", entry.CreatedByName);
            Assert.Null(entry.StateId);
            Assert.Equal(s_now.UtcDateTime, entry.CreatedAt);
        }
    }

    // AC4
    [Fact]
    public async Task Apply_changes_no_other_work_item()
    {
        await SeedBothIncorrectAsync();
        var bystanders = new[]
        {
            // The 2026 numbers the corrections restore the sequence from.
            await SeedAsync("A26ER5005270246PL"),
            await SeedAsync("A26ER5001050197PL"),
            // Same organisation, different material: a different identity.
            await SeedAsync("A27ER5005270322GL"),
            // Same organisation as an exporter: a different identity.
            await SeedAsync("A27EX5005270322PL"),
            // Unrelated organisation.
            await SeedAsync("A27ER5009990322PL"),
        };

        await RunAsync(BuildSut());

        foreach (var bystander in bystanders)
        {
            await AssertUntouchedAsync(bystander);
        }
    }

    // AC5
    [Fact]
    public async Task A_second_run_changes_nothing_and_adds_no_second_entry()
    {
        var (infinity, plasgran) = await SeedBothIncorrectAsync();
        await RunAsync(BuildSut());
        var afterFirst = new[] { await ReloadAsync(infinity), await ReloadAsync(plasgran) };

        await RunAsync(BuildSut());

        foreach (var first in afterFirst)
        {
            await AssertUntouchedAsync(first);
        }
        Assert.Equal(
            2,
            _logger.Records.Count(r => r.Message.Contains("already carries", StringComparison.Ordinal))
        );
    }

    [Fact]
    public async Task An_already_corrected_target_is_skipped_while_the_other_is_corrected()
    {
        var infinity = await SeedAsync(InfinityCorrected);
        var plasgran = await SeedAsync(PlasgranIncorrect);

        await RunAsync(BuildSut());

        await AssertUntouchedAsync(infinity);
        Assert.Equal(PlasgranCorrected, (await ReloadAsync(plasgran)).Payload["accreditationId"].AsString);
    }

    // ------------------------------- abort -------------------------------

    [Fact]
    public async Task A_target_matching_neither_value_aborts_the_whole_run_before_any_write()
    {
        // Infinity (planned first) is correctable; PLASGRAN carries a third
        // sequence. Nothing may be written, including Infinity's correction.
        var infinity = await SeedAsync(InfinityIncorrect);
        var plasgran = await SeedAsync("A27ER5001050999PL");

        await RunAsync(BuildSut());

        await AssertUntouchedAsync(infinity);
        await AssertUntouchedAsync(plasgran);
        var error = Assert.Single(_logger.Records, r => r.Level == LogLevel.Error);
        Assert.Contains("ABORTED", error.Message);
        Assert.Contains("A27ER5001050999PL", error.Message);
    }

    [Fact]
    public async Task A_missing_target_aborts_the_whole_run_before_any_write()
    {
        var infinity = await SeedAsync(InfinityIncorrect);

        await RunAsync(BuildSut());

        await AssertUntouchedAsync(infinity);
        var error = Assert.Single(_logger.Records, r => r.Level == LogLevel.Error);
        Assert.Contains("found 0", error.Message);
    }

    [Fact]
    public async Task More_than_one_candidate_aborts_rather_than_guessing()
    {
        // E.g. a second plastic site for the same organisation.
        var (infinity, plasgran) = await SeedBothIncorrectAsync();
        var secondSite = await SeedAsync("A27ER5005270500PL");

        await RunAsync(BuildSut());

        await AssertUntouchedAsync(infinity);
        await AssertUntouchedAsync(plasgran);
        await AssertUntouchedAsync(secondSite);
        var error = Assert.Single(_logger.Records, r => r.Level == LogLevel.Error);
        Assert.Contains("found 2", error.Message);
    }

    [Fact]
    public async Task A_number_changed_after_planning_is_not_overwritten()
    {
        // The atomic update is filtered on the incorrect value, so a number
        // that changed between planning and writing is left as it is and gets
        // no audit entry.
        var (infinity, _) = await SeedBothIncorrectAsync();
        var racing = new RacingPersistence(_persistence, infinity.Id, "A27ER5005270777PL");

        await BuildSut().ApplyAsync(racing, TestContext.Current.CancellationToken);

        var reloaded = await ReloadAsync(infinity);
        Assert.Equal("A27ER5005270777PL", reloaded.Payload["accreditationId"].AsString);
        Assert.DoesNotContain(reloaded.AuditLog, e => e.Action == "accreditation-number-corrected");
        Assert.Contains(_logger.Records, r => r.Level == LogLevel.Warning && r.Message.Contains(infinity.Id.ToString(), StringComparison.Ordinal));
    }

    /// <summary>Changes one item's number just before the migration's first write.</summary>
    private sealed class RacingPersistence(WorkItemPersistence inner, Guid raceId, string raceValue)
        : IWorkItemPersistence
    {
        private bool _raced;

        public async Task<bool> CorrectPayloadFieldAsync(
            Guid workItemId,
            string fieldName,
            BsonValue expectedValue,
            BsonValue newValue,
            WorkItemAuditEntry auditEntry,
            CancellationToken cancellationToken = default
        )
        {
            if (!_raced)
            {
                _raced = true;
                await inner.SetPayloadFieldAsync(raceId, fieldName, raceValue, cancellationToken);
            }
            return await inner.CorrectPayloadFieldAsync(
                workItemId,
                fieldName,
                expectedValue,
                newValue,
                auditEntry,
                cancellationToken
            );
        }

        public Task<IReadOnlyList<WorkItem>> FindByPayloadFieldPrefixAsync(
            string typeId,
            string fieldName,
            string prefix,
            CancellationToken cancellationToken = default
        ) => inner.FindByPayloadFieldPrefixAsync(typeId, fieldName, prefix, cancellationToken);

        public Task CreateAsync(WorkItem workItem, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> CreateIfAbsentAsync(WorkItem workItem, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkItemPage> QueryAsync(WorkItemQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReplaceAsync(WorkItem workItem, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> SetPayloadFieldAsync(
            Guid workItemId,
            string fieldName,
            BsonValue value,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<WorkItem?> FindByOperatorApplicationIdAsync(
            string typeId,
            string operatorApplicationId,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }
}
