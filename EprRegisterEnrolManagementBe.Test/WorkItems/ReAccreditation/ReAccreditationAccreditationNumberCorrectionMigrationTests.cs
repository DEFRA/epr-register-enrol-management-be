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
/// RA-691 / RA-763: the accreditation number correction, run against real (ephemeral)
/// Mongo through the real <see cref="WorkItemPersistence"/> so the atomic
/// filtered update and "nothing else changed" are proved on the wire.
/// </summary>
public sealed class ReAccreditationAccreditationNumberCorrectionMigrationTests : IAsyncDisposable
{
    private const string InfinityIncorrect = "A27ER5005270322PL";
    private const string InfinityCorrected = "A27ER5005270246PL";
    private const string PlasgranIncorrect = "A27ER5001050321PL";
    private const string PlasgranCorrected = "A27ER5001050197PL";
    private const string KronospanIncorrect = "A27WR5001080075WO";
    private const string KronospanCorrected = "A27WR5001080008WO";
    private const string ClearfieldIncorrect = "A27ER5003240079PL";
    private const string ClearfieldCorrected = "A27ER5003240164PL";

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

    private async Task<(
        WorkItem Infinity,
        WorkItem Plasgran,
        WorkItem Kronospan,
        WorkItem Clearfield
    )> SeedAllIncorrectAsync() =>
        (
            await SeedAsync(InfinityIncorrect, "Infinity Plastic Solutions Ltd"),
            await SeedAsync(PlasgranIncorrect, "PLASGRAN LIMITED"),
            await SeedAsync(KronospanIncorrect, "Kronospan Limited"),
            await SeedAsync(ClearfieldIncorrect, "CLEARFIELD ENVIROTECH LIMITED")
        );

    /// <summary>
    /// Production after RA-691 was applied: the three RA-691 items already
    /// carry their corrected numbers; only Clearfield (RA-763) is outstanding.
    /// </summary>
    private async Task<(WorkItem[] Ra691, WorkItem Clearfield)> SeedRa691AlreadyCorrectedAsync() =>
        (
            [
                await SeedAsync(InfinityCorrected, "Infinity Plastic Solutions Ltd"),
                await SeedAsync(PlasgranCorrected, "PLASGRAN LIMITED"),
                await SeedAsync(KronospanCorrected, "Kronospan Limited"),
            ],
            await SeedAsync(ClearfieldIncorrect, "CLEARFIELD ENVIROTECH LIMITED")
        );

    private int CountLogs(string fragment) =>
        _logger.Records.Count(r => r.Message.Contains(fragment, StringComparison.Ordinal));

    private static void AssertSingleCorrectionEntry(WorkItem reloaded, string from, string to, string reason)
    {
        var entry = Assert.Single(reloaded.AuditLog, e => e.Action == "accreditation-number-corrected");
        Assert.Equal("Accreditation number corrected", entry.ActionDisplayName);
        Assert.Equal(
            new Dictionary<string, string?> { ["from"] = from, ["to"] = to, ["reason"] = reason },
            entry.Details
        );
        Assert.Equal("migration", entry.CreatedBy);
        Assert.Equal("Migration: RA-691 accreditation number correction", entry.CreatedByName);
        Assert.Null(entry.StateId);
        Assert.Equal(s_now.UtcDateTime, entry.CreatedAt);
    }

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
        var (infinity, plasgran, kronospan, clearfield) = await SeedAllIncorrectAsync();

        await RunAsync(BuildSut(enabled: false));

        await AssertUntouchedAsync(infinity);
        await AssertUntouchedAsync(plasgran);
        await AssertUntouchedAsync(kronospan);
        await AssertUntouchedAsync(clearfield);
        Assert.Empty(_logger.Records);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Apply_without_a_spot_check_logs_an_error_and_runs_as_a_dry_run(string? confirmedBy)
    {
        var (infinity, plasgran, kronospan, clearfield) = await SeedAllIncorrectAsync();

        await RunAsync(BuildSut(confirmedBy: confirmedBy));

        await AssertUntouchedAsync(infinity);
        await AssertUntouchedAsync(plasgran);
        await AssertUntouchedAsync(kronospan);
        await AssertUntouchedAsync(clearfield);
        var error = Assert.Single(_logger.Records, r => r.Level == LogLevel.Error);
        Assert.Contains(
            ReAccreditationAccreditationNumberCorrectionMigration.SpotCheckConfirmedByConfigKey,
            error.Message
        );
        Assert.Equal(4, CountLogs("DRY RUN would correct"));
    }

    [Fact]
    public async Task Dry_run_does_not_need_a_spot_check()
    {
        var (infinity, plasgran, kronospan, clearfield) = await SeedAllIncorrectAsync();

        await RunAsync(BuildSut(confirmedBy: null, apply: false));

        await AssertUntouchedAsync(infinity);
        await AssertUntouchedAsync(plasgran);
        await AssertUntouchedAsync(kronospan);
        await AssertUntouchedAsync(clearfield);
        Assert.DoesNotContain(_logger.Records, r => r.Level == LogLevel.Error);
        Assert.Equal(4, CountLogs("DRY RUN would correct"));
    }

    // AC1
    [Fact]
    public async Task Dry_run_logs_exactly_the_four_changes_and_writes_nothing()
    {
        var (infinity, plasgran, kronospan, clearfield) = await SeedAllIncorrectAsync();

        await RunAsync(BuildSut(apply: false));

        await AssertUntouchedAsync(infinity);
        await AssertUntouchedAsync(plasgran);
        await AssertUntouchedAsync(kronospan);
        await AssertUntouchedAsync(clearfield);
        var wouldCorrect = _logger
            .Records.Where(r => r.Message.Contains("DRY RUN would correct", StringComparison.Ordinal))
            .Select(r => r.Message)
            .ToList();
        Assert.Equal(4, wouldCorrect.Count);
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
        Assert.Contains(
            wouldCorrect,
            m => m.EndsWith(
                $"Kronospan Limited (500108) work item {kronospan.Id}: {KronospanIncorrect} → {KronospanCorrected}",
                StringComparison.Ordinal
            )
        );
        Assert.Contains(
            wouldCorrect,
            m => m.EndsWith(
                $"CLEARFIELD ENVIROTECH LIMITED (500324) work item {clearfield.Id}: {ClearfieldIncorrect} → {ClearfieldCorrected}",
                StringComparison.Ordinal
            )
        );
        Assert.Equal(1, CountLogs("DRY RUN complete. Would correct: 4."));
    }

    // RA-763: production's state once RA-691 is applied.
    [Fact]
    public async Task Dry_run_after_RA_691_skips_its_three_items_and_reports_only_Clearfield()
    {
        var (ra691, clearfield) = await SeedRa691AlreadyCorrectedAsync();

        await RunAsync(BuildSut(apply: false));

        foreach (var item in ra691)
        {
            await AssertUntouchedAsync(item);
        }
        await AssertUntouchedAsync(clearfield);
        Assert.Equal(3, CountLogs("already carries"));
        var wouldCorrect = Assert.Single(
            _logger.Records,
            r => r.Message.Contains("DRY RUN would correct", StringComparison.Ordinal)
        );
        Assert.EndsWith(
            $"CLEARFIELD ENVIROTECH LIMITED (500324) work item {clearfield.Id}: {ClearfieldIncorrect} → {ClearfieldCorrected}",
            wouldCorrect.Message,
            StringComparison.Ordinal
        );
        Assert.Equal(1, CountLogs("DRY RUN complete. Would correct: 1."));
    }

    // ------------------------------- apply -------------------------------

    // AC2 + AC3
    [Fact]
    public async Task Apply_corrects_all_four_numbers_and_appends_one_audit_entry_each_with_its_ticket_as_the_reason()
    {
        var (infinity, plasgran, kronospan, clearfield) = await SeedAllIncorrectAsync();

        await RunAsync(BuildSut());

        foreach (var (seeded, from, to, reason) in new[]
        {
            (infinity, InfinityIncorrect, InfinityCorrected, "RA-691"),
            (plasgran, PlasgranIncorrect, PlasgranCorrected, "RA-691"),
            (kronospan, KronospanIncorrect, KronospanCorrected, "RA-691"),
            (clearfield, ClearfieldIncorrect, ClearfieldCorrected, "RA-763"),
        })
        {
            var reloaded = await ReloadAsync(seeded);
            Assert.Equal(to, reloaded.Payload["accreditationId"].AsString);
            Assert.Equal(ReAccreditationType.ApprovedStateId, reloaded.StateId);
            Assert.Equal(seeded.LastModifiedAt, reloaded.LastModifiedAt);
            Assert.Equal(seeded.Version + 1, reloaded.Version);

            // The earlier entry is history and keeps the old number.
            Assert.Equal(from, reloaded.AuditLog[0].Details["accreditationId"]);

            AssertSingleCorrectionEntry(reloaded, from, to, reason);
        }
        Assert.Equal(1, CountLogs("APPLY complete. Corrected: 4."));
    }

    // RA-763: production's state once RA-691 is applied.
    [Fact]
    public async Task Apply_after_RA_691_skips_its_three_items_and_corrects_Clearfield_with_reason_RA_763()
    {
        var (ra691, clearfield) = await SeedRa691AlreadyCorrectedAsync();

        await RunAsync(BuildSut());

        foreach (var item in ra691)
        {
            await AssertUntouchedAsync(item);
        }
        var reloaded = await ReloadAsync(clearfield);
        Assert.Equal(ClearfieldCorrected, reloaded.Payload["accreditationId"].AsString);
        Assert.Equal(ReAccreditationType.ApprovedStateId, reloaded.StateId);
        Assert.Equal(clearfield.Version + 1, reloaded.Version);
        Assert.Equal(ClearfieldIncorrect, reloaded.AuditLog[0].Details["accreditationId"]);
        AssertSingleCorrectionEntry(reloaded, ClearfieldIncorrect, ClearfieldCorrected, "RA-763");
        Assert.Equal(3, CountLogs("already carries"));
        Assert.Equal(1, CountLogs("APPLY complete. Corrected: 1."));
    }

    // AC4
    [Fact]
    public async Task Apply_changes_no_other_work_item()
    {
        await SeedAllIncorrectAsync();
        var bystanders = new[]
        {
            // The 2026 numbers the corrections restore the sequence from.
            await SeedAsync("A26ER5005270246PL"),
            await SeedAsync("A26ER5001050197PL"),
            await SeedAsync("A26WR5001080008WO"),
            await SeedAsync("A26ER5003240164PL"),
            // Same organisation, different material: a different identity.
            await SeedAsync("A27ER5005270322GL"),
            await SeedAsync("A27WR5001080075PL"),
            await SeedAsync("A27ER5003240079GL"),
            // Same organisation as an exporter: a different identity.
            await SeedAsync("A27EX5005270322PL"),
            await SeedAsync("A27WX5001080075WO"),
            await SeedAsync("A27EX5003240079PL"),
            // Same organisation and material, different nation: a different identity.
            await SeedAsync("A27ER5001080075WO"),
            await SeedAsync("A27WR5003240079PL"),
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
        var (infinity, plasgran, kronospan, clearfield) = await SeedAllIncorrectAsync();
        await RunAsync(BuildSut());
        var afterFirst = new[]
        {
            await ReloadAsync(infinity),
            await ReloadAsync(plasgran),
            await ReloadAsync(kronospan),
            await ReloadAsync(clearfield),
        };

        await RunAsync(BuildSut());

        foreach (var first in afterFirst)
        {
            await AssertUntouchedAsync(first);
        }
        Assert.Equal(4, CountLogs("already carries"));
        Assert.Equal(1, CountLogs("APPLY complete. Corrected: 0."));
    }

    [Fact]
    public async Task An_already_corrected_target_is_skipped_while_the_others_are_corrected()
    {
        var infinity = await SeedAsync(InfinityCorrected);
        var plasgran = await SeedAsync(PlasgranIncorrect);
        var kronospan = await SeedAsync(KronospanIncorrect);
        var clearfield = await SeedAsync(ClearfieldIncorrect);

        await RunAsync(BuildSut());

        await AssertUntouchedAsync(infinity);
        Assert.Equal(PlasgranCorrected, (await ReloadAsync(plasgran)).Payload["accreditationId"].AsString);
        Assert.Equal(KronospanCorrected, (await ReloadAsync(kronospan)).Payload["accreditationId"].AsString);
        Assert.Equal(ClearfieldCorrected, (await ReloadAsync(clearfield)).Payload["accreditationId"].AsString);
    }

    // ------------------------------- abort -------------------------------

    [Fact]
    public async Task A_target_matching_neither_value_aborts_the_whole_run_before_any_write()
    {
        // The three RA-691 targets (planned first) are correctable; Clearfield
        // (planned last) carries an unexpected sequence. Nothing may be
        // written, including the three earlier corrections.
        var infinity = await SeedAsync(InfinityIncorrect);
        var plasgran = await SeedAsync(PlasgranIncorrect);
        var kronospan = await SeedAsync(KronospanIncorrect);
        var clearfield = await SeedAsync("A27ER5003240999PL");

        await RunAsync(BuildSut());

        await AssertUntouchedAsync(infinity);
        await AssertUntouchedAsync(plasgran);
        await AssertUntouchedAsync(kronospan);
        await AssertUntouchedAsync(clearfield);
        var error = Assert.Single(_logger.Records, r => r.Level == LogLevel.Error);
        Assert.Contains("ABORTED", error.Message);
        Assert.Contains("CLEARFIELD ENVIROTECH LIMITED (500324)", error.Message);
        Assert.Contains("A27ER5003240999PL", error.Message);
    }

    [Fact]
    public async Task A_missing_target_aborts_the_whole_run_before_any_write()
    {
        // Clearfield (planned last) is missing; the three earlier, correctable
        // targets must not be written.
        var infinity = await SeedAsync(InfinityIncorrect);
        var plasgran = await SeedAsync(PlasgranIncorrect);
        var kronospan = await SeedAsync(KronospanIncorrect);

        await RunAsync(BuildSut());

        await AssertUntouchedAsync(infinity);
        await AssertUntouchedAsync(plasgran);
        await AssertUntouchedAsync(kronospan);
        var error = Assert.Single(_logger.Records, r => r.Level == LogLevel.Error);
        Assert.Contains("CLEARFIELD ENVIROTECH LIMITED (500324)", error.Message);
        Assert.Contains("found 0", error.Message);
    }

    [Fact]
    public async Task More_than_one_candidate_aborts_rather_than_guessing()
    {
        // E.g. a second plastic site for the same organisation.
        var (infinity, plasgran, kronospan, clearfield) = await SeedAllIncorrectAsync();
        var secondSite = await SeedAsync("A27ER5003240500PL");

        await RunAsync(BuildSut());

        await AssertUntouchedAsync(infinity);
        await AssertUntouchedAsync(plasgran);
        await AssertUntouchedAsync(kronospan);
        await AssertUntouchedAsync(clearfield);
        await AssertUntouchedAsync(secondSite);
        var error = Assert.Single(_logger.Records, r => r.Level == LogLevel.Error);
        Assert.Contains("CLEARFIELD ENVIROTECH LIMITED (500324)", error.Message);
        Assert.Contains("found 2", error.Message);
    }

    [Fact]
    public async Task A_number_changed_after_planning_is_not_overwritten()
    {
        // The atomic update is filtered on the incorrect value, so a number
        // that changed between planning and writing is left as it is and gets
        // no audit entry.
        var (infinity, _, _, _) = await SeedAllIncorrectAsync();
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

        public Task<WorkItem?> FindBySubmissionKeyAsync(
            string typeId,
            string payloadField,
            string value,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }
}
