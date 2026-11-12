using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Migration = EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReAccreditationOrganisationNameCorrectionMigration;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-764: the organisation 500069 rename, run against real (ephemeral) Mongo
/// through the real <see cref="WorkItemPersistence"/>, so the filtered update,
/// "nothing else changed" and "search finds the new name" are proved on the
/// wire.
/// </summary>
public sealed class ReAccreditationOrganisationNameCorrectionMigrationTests : IAsyncDisposable
{
    private static readonly DateTime s_submittedAt = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime s_lastModifiedAt = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _clientFactory;
    private readonly WorkItemPersistence _persistence;
    private readonly ListLogger<Migration> _logger = new();

    public ReAccreditationOrganisationNameCorrectionMigrationTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("ra764");
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new WorkItemPersistence(_clientFactory, NullLoggerFactory.Instance);
    }

    public async ValueTask DisposeAsync() =>
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

    // ------------------------------- helpers -------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<WorkItem> SeedAsync(
        string organisationName = Migration.OldName,
        string registrationNumber = Migration.RegistrationNumber,
        int accreditationYear = Migration.AccreditationYear,
        Action<BsonDocument>? customise = null
    )
    {
        var item = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "assessment-in-progress",
            SubmittedAt = s_submittedAt,
            LastModifiedAt = s_lastModifiedAt,
            SubmittedBy = "operator@example.com",
            AssignedToId = "case-officer-1",
            AssignedToName = "Case Officer",
        };
        var payload = new BsonDocument
        {
            ["applicationReference"] = "APP-" + Guid.NewGuid().ToString("N")[..8],
            ["organisationName"] = organisationName,
            ["operatorOrgNumber"] = Migration.OrganisationNumber,
            ["registrationNumber"] = registrationNumber,
            ["accreditationYear"] = accreditationYear,
            ["material"] = "glass",
            ["siteAddress"] = "Encirc Ltd, Site Road, Derrylin",
        };
        customise?.Invoke(payload);
        item.ReplacePayload(payload);
        item.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = "assigned",
                ActionDisplayName = "Assigned",
                CreatedAt = s_lastModifiedAt,
                Details = new Dictionary<string, string?> { ["organisationName"] = organisationName },
            }
        );
        await _persistence.CreateAsync(item, Ct);
        return item;
    }

    private Migration NewMigration(bool enabled = true, bool apply = true, string? confirmedBy = "a.reviewer")
    {
        var settings = new Dictionary<string, string?>();
        if (enabled)
        {
            settings[Migration.EnabledConfigKey] = "true";
        }
        if (apply)
        {
            settings[Migration.ApplyConfigKey] = "true";
        }
        if (confirmedBy is not null)
        {
            settings[Migration.SpotCheckConfirmedByConfigKey] = confirmedBy;
        }

        var services = new ServiceCollection()
            .AddSingleton<IWorkItemPersistence>(_persistence)
            .BuildServiceProvider();
        return new Migration(
            services,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            _logger
        );
    }

    private async Task<WorkItem> ReloadAsync(WorkItem item) =>
        (await _persistence.GetByIdAsync(item.Id, Ct))!;

    private async Task<string?> NameOfAsync(WorkItem item) =>
        (await ReloadAsync(item)).Payload["organisationName"].AsString;

    private bool Logged(LogLevel level, string fragment) =>
        _logger.Records.Any(r => r.Level == level && r.Message.Contains(fragment, StringComparison.Ordinal));

    // ------------------------------- gates -------------------------------

    [Fact]
    public async Task Gate_off_does_nothing()
    {
        var target = await SeedAsync();

        var result = await NewMigration(enabled: false).ApplyAsync(_persistence, Ct);

        Assert.Equal(Migration.RunResult.Disabled, result);
        Assert.Equal(Migration.OldName, await NameOfAsync(target));
        Assert.Equal(0, (await ReloadAsync(target)).Version);
        Assert.Empty(_logger.Records);
    }

    [Fact]
    public async Task Enabled_alone_is_a_dry_run_that_writes_nothing()
    {
        var target = await SeedAsync();

        var result = await NewMigration(apply: false).ApplyAsync(_persistence, Ct);

        Assert.Equal(Migration.RunResult.DryRun, result);
        Assert.Equal(Migration.OldName, await NameOfAsync(target));
        Assert.Equal(0, (await ReloadAsync(target)).Version);
        Assert.True(Logged(LogLevel.Information, "DRY RUN would rename"));
    }

    [Fact]
    public async Task Apply_without_a_spot_check_name_stays_a_dry_run()
    {
        var target = await SeedAsync();

        var result = await NewMigration(confirmedBy: "  ").ApplyAsync(_persistence, Ct);

        Assert.Equal(Migration.RunResult.DryRun, result);
        Assert.Equal(Migration.OldName, await NameOfAsync(target));
        Assert.True(Logged(LogLevel.Error, "running as a DRY RUN"));
    }

    // ------------------------------- apply -------------------------------

    [Fact]
    public async Task Apply_renames_the_2027_case_and_changes_nothing_else()
    {
        var target = await SeedAsync(
            customise: p => p["accreditationId"] = "A27NR5000690003GL"
        );
        var before = await ReloadAsync(target);

        var result = await NewMigration().ApplyAsync(_persistence, Ct);

        Assert.Equal(Migration.RunResult.Applied, result);
        var after = await ReloadAsync(target);
        Assert.Equal(Migration.NewName, after.Payload["organisationName"].AsString);

        // Only the name (and the concurrency version) moved.
        var expectedPayload = before.Payload.DeepClone().AsBsonDocument;
        expectedPayload["organisationName"] = Migration.NewName;
        Assert.Equal(expectedPayload, after.Payload);
        Assert.Equal(before.Version + 1, after.Version);
        Assert.Equal(before.StateId, after.StateId);
        Assert.Equal(before.LastModifiedAt, after.LastModifiedAt);
        Assert.Equal(before.AssignedToId, after.AssignedToId);
        Assert.Equal(before.AssignedToName, after.AssignedToName);
        Assert.Equal(before.TemplateVersion, after.TemplateVersion);

        // No audit entry written, and the existing one (which quotes the old
        // name) is history and is not rewritten.
        var entry = Assert.Single(after.AuditLog);
        Assert.Equal(Migration.OldName, entry.Details!["organisationName"]);
        Assert.Empty(after.Notes);
    }

    [Fact]
    public async Task Apply_renames_every_2027_case_for_the_registration()
    {
        var withdrawn = await SeedAsync(customise: p => p["withdrawnReason"] = "resubmitting");
        var resubmitted = await SeedAsync();

        await NewMigration().ApplyAsync(_persistence, Ct);

        Assert.Equal(Migration.NewName, await NameOfAsync(withdrawn));
        Assert.Equal(Migration.NewName, await NameOfAsync(resubmitted));
    }

    [Fact]
    public async Task Only_500069s_2027_cases_are_touched()
    {
        var target = await SeedAsync();
        var sameNameOtherRegistration = await SeedAsync(
            registrationNumber: "R26NR5000700001GL",
            customise: p => p["operatorOrgNumber"] = 500070
        );
        var same2026Registration = await SeedAsync(accreditationYear: 2026);
        var unrelated = await SeedAsync(
            organisationName: "Some Other Glass Ltd",
            registrationNumber: "R26ER5001230001GR",
            customise: p => p["operatorOrgNumber"] = 500123
        );

        await NewMigration().ApplyAsync(_persistence, Ct);

        Assert.Equal(Migration.NewName, await NameOfAsync(target));
        Assert.Equal(Migration.OldName, await NameOfAsync(sameNameOtherRegistration));
        Assert.Equal(Migration.OldName, await NameOfAsync(same2026Registration));
        Assert.Equal("Some Other Glass Ltd", await NameOfAsync(unrelated));
        Assert.Equal(0, (await ReloadAsync(same2026Registration)).Version);
        Assert.Equal(0, (await ReloadAsync(unrelated)).Version);
    }

    [Fact]
    public async Task A_case_without_operatorOrgNumber_or_accreditation_number_is_matched_by_registration()
    {
        var target = await SeedAsync(customise: p => p.Remove("operatorOrgNumber"));

        await NewMigration().ApplyAsync(_persistence, Ct);

        Assert.Equal(Migration.NewName, await NameOfAsync(target));
    }

    // ------------------------------- idempotency -------------------------------

    [Fact]
    public async Task A_rerun_is_a_no_op()
    {
        var target = await SeedAsync();
        await NewMigration().ApplyAsync(_persistence, Ct);
        var afterFirst = await ReloadAsync(target);

        var result = await NewMigration().ApplyAsync(_persistence, Ct);

        Assert.Equal(Migration.RunResult.NothingToDo, result);
        var afterSecond = await ReloadAsync(target);
        Assert.Equal(afterFirst.Version, afterSecond.Version);
        Assert.Equal(afterFirst.Payload, afterSecond.Payload);
        Assert.Single(afterSecond.AuditLog);
    }

    [Fact]
    public async Task An_already_renamed_case_is_skipped_without_a_write()
    {
        var target = await SeedAsync(organisationName: Migration.NewName);

        var result = await NewMigration().ApplyAsync(_persistence, Ct);

        Assert.Equal(Migration.RunResult.NothingToDo, result);
        Assert.Equal(0, (await ReloadAsync(target)).Version);
        Assert.True(Logged(LogLevel.Information, "already carries"));
    }

    // ------------------------------- aborts -------------------------------

    [Fact]
    public async Task No_2027_case_aborts()
    {
        var only2026 = await SeedAsync(accreditationYear: 2026);

        var result = await NewMigration().ApplyAsync(_persistence, Ct);

        Assert.Equal(Migration.RunResult.Aborted, result);
        Assert.Equal(Migration.OldName, await NameOfAsync(only2026));
        Assert.True(Logged(LogLevel.Error, "ABORTED, nothing written"));
    }

    public static TheoryData<string, Action<BsonDocument>> UnexpectedStates =>
        new()
        {
            { "unexpected name", p => p["organisationName"] = "Encirc Limited" },
            { "other org number", p => p["operatorOrgNumber"] = 500070 },
            { "other org's accreditation number", p => p["accreditationId"] = "A27NR5000700003GL" },
            { "other material's accreditation number", p => p["accreditationId"] = "A27NR5000690003PL" },
            { "no accreditation year", p => p.Remove("accreditationYear") },
        };

    [Theory]
    [MemberData(nameof(UnexpectedStates))]
    public async Task An_unexpected_state_on_any_case_aborts_before_any_write(
        string scenario,
        Action<BsonDocument> customise
    )
    {
        // The good case is planned first, so an abort that wrote as it went
        // would leave it renamed.
        var good = await SeedAsync();
        var bad = await SeedAsync(customise: customise);

        var result = await NewMigration().ApplyAsync(_persistence, Ct);

        Assert.True(result == Migration.RunResult.Aborted, scenario);
        Assert.Equal(Migration.OldName, await NameOfAsync(good));
        Assert.Equal(0, (await ReloadAsync(good)).Version);
        Assert.Equal(0, (await ReloadAsync(bad)).Version);
    }

    // ------------------------------- search -------------------------------

    [Fact]
    public async Task After_apply_case_management_search_finds_the_case_by_its_new_name_only()
    {
        var target = await SeedAsync();
        await SeedAsync(
            organisationName: "Some Other Glass Ltd",
            registrationNumber: "R26ER5001230001GR",
            customise: p => p["operatorOrgNumber"] = 500123
        );

        await NewMigration().ApplyAsync(_persistence, Ct);

        // The Applications page's "Organisation name or ID" box (substring,
        // case-insensitive) and the $text-backed orgName filter.
        var byNewName = await _persistence.QueryAsync(new WorkItemQuery(Organisation: "vidrala uk & ireland"), Ct);
        var byOldName = await _persistence.QueryAsync(new WorkItemQuery(Organisation: "Encirc"), Ct);
        var byText = await _persistence.QueryAsync(new WorkItemQuery(OrgName: Migration.NewName), Ct);
        var byOldText = await _persistence.QueryAsync(new WorkItemQuery(OrgName: Migration.OldName), Ct);

        Assert.Equal(target.Id, Assert.Single(byNewName.Items).Id);
        Assert.Empty(byOldName.Items);
        Assert.Equal(target.Id, Assert.Single(byText.Items).Id);
        Assert.Empty(byOldText.Items);
    }

    // ------------------------------- races and failures -------------------------------

    [Fact]
    public async Task A_name_changed_between_planning_and_writing_is_not_overwritten()
    {
        var target = await SeedAsync();
        var racing = Substitute.For<IWorkItemPersistence>();
        racing
            .FindByPayloadFieldAsync(ReAccreditationType.Id, "registrationNumber", Migration.RegistrationNumber, Arg.Any<CancellationToken>())
            .Returns([await ReloadAsync(target)]);
        racing
            .CompareAndSetPayloadFieldAsync(default, default!, default!, default!, default)
            .ReturnsForAnyArgs(false);

        var result = await NewMigration().ApplyAsync(racing, Ct);

        Assert.Equal(Migration.RunResult.Applied, result);
        Assert.True(Logged(LogLevel.Warning, "no longer carries"));
    }

    [Fact]
    public async Task StartedAsync_logs_a_failure_and_does_not_throw()
    {
        var broken = Substitute.For<IServiceProvider>();
        broken.GetService(typeof(IWorkItemPersistence)).Throws(new InvalidOperationException("mongo down"));
        var migration = new Migration(
            broken,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [Migration.EnabledConfigKey] = "true" })
                .Build(),
            _logger
        );

        await migration.StartedAsync(Ct);

        Assert.True(Logged(LogLevel.Warning, "continuing startup"));
    }

    [Fact]
    public async Task StartedAsync_with_the_gate_off_does_not_touch_persistence()
    {
        var provider = Substitute.For<IServiceProvider>();
        var migration = new Migration(provider, new ConfigurationBuilder().Build(), _logger);

        await migration.StartedAsync(Ct);
        await migration.StartingAsync(Ct);
        await migration.StartAsync(Ct);
        await migration.StoppingAsync(Ct);
        await migration.StopAsync(Ct);
        await migration.StoppedAsync(Ct);

        provider.DidNotReceiveWithAnyArgs().GetService(default!);
    }

    [Fact]
    public async Task StartedAsync_applies_through_the_registered_persistence()
    {
        var target = await SeedAsync();

        await NewMigration().StartedAsync(Ct);

        Assert.Equal(Migration.NewName, await NameOfAsync(target));
    }
}
