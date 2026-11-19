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
using Migration = EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReAccreditationLastYearsAccreditationBackfillMigration;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-784: the backfill of last year's accreditation number, run against real (ephemeral) Mongo through the real
/// <see cref="WorkItemPersistence"/> and rows from the real public register, so
/// the filtered updates and their audit entries are proved on the wire.
/// </summary>
public sealed class ReAccreditationLastYearsAccreditationBackfillMigrationTests : IAsyncDisposable
{
    // Egger (UK) Limited: accredited in 2026, with a different sequence from its registration.
    private const string EggerRegistration = "R26ER5000270036WO";
    private const string EggerAccreditation = "A26ER5000270063WO";

    // DTS Trading LTD: registered only in 2026.
    private const string DtsRegistration = "R26ER5002370119PL";

    // Clearfield Envirotech: issued a number derived from its registration (corrected by RA-785, not here).
    private const string ClearfieldRegistration = "R26ER5003240079PL";
    private const string ClearfieldAccreditation = "A26ER5003240164PL";
    private const string ClearfieldIssued = "A27ER5003240079PL";

    // Published rows that look wrong but are used as published.
    private const string JpRecyclingRegistration = "R26WR5003020027PL";
    private const string JpRecyclingAccreditation = "A26WR5005320025PL";
    private const string ComhairleRegistration = "R26SR5005410022GO";
    private const string ComhairleAccreditation = "A26SR5005410022GL";
    private const string EncircRegistration = "R26ER5002260055GR";
    private const string EncircAccreditation = "A26ER5002260023GR";
    private const string JAndAYoungRegistration = "R26WR5000950025PL";

    private static readonly DateTime s_now = new(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _clientFactory;
    private readonly WorkItemPersistence _persistence;
    private readonly ListLogger<Migration> _logger = new();

    public ReAccreditationLastYearsAccreditationBackfillMigrationTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("ra763");
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new WorkItemPersistence(_clientFactory, NullLoggerFactory.Instance);
    }

    public async ValueTask DisposeAsync() =>
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

    // ------------------------------- helpers -------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<WorkItem> SeedAsync(
        string? registrationNumber,
        int? orgNumber,
        string material = "plastic",
        int accreditationYear = Migration.AccreditationYear,
        Action<BsonDocument>? customise = null
    )
    {
        var item = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "assessment-in-progress",
            SubmittedAt = s_now.AddDays(-30),
            LastModifiedAt = s_now.AddDays(-10),
            SubmittedBy = "operator@example.com",
        };
        var payload = new BsonDocument
        {
            ["applicationReference"] = "APP-" + Guid.NewGuid().ToString("N")[..8],
            ["accreditationYear"] = accreditationYear,
            ["material"] = material,
        };
        if (registrationNumber is not null)
        {
            payload["registrationNumber"] = registrationNumber;
        }
        if (orgNumber is not null)
        {
            payload["operatorOrgNumber"] = orgNumber.Value;
        }
        customise?.Invoke(payload);
        item.ReplacePayload(payload);
        await _persistence.CreateAsync(item, Ct);
        return item;
    }

    private Task<WorkItem> EggerAsync(Action<BsonDocument>? customise = null) =>
        SeedAsync(EggerRegistration, 500027, "wood", customise: customise);

    private Task<WorkItem> ClearfieldAsync(string issued = ClearfieldIssued) =>
        SeedAsync(
            ClearfieldRegistration,
            500324,
            customise: p => p[ReAccreditationAccreditationNumber.PayloadKey] = issued
        );

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
            _logger,
            new FixedTimeProvider(s_now)
        );
    }

    private Task<Migration.RunResult> RunAsync(bool apply = true) =>
        NewMigration(apply: apply).ApplyAsync(_persistence, Ct);

    private async Task<WorkItem> ReloadAsync(WorkItem item) =>
        (await _persistence.GetByIdAsync(item.Id, Ct))!;

    private static BsonDocument? LastYears(WorkItem item) =>
        item.Payload.TryGetValue(Migration.PayloadKey, out var value) && value.IsBsonDocument
            ? value.AsBsonDocument
            : null;

    private bool Logged(LogLevel level, string fragment) =>
        _logger.Records.Any(r => r.Level == level && r.Message.Contains(fragment, StringComparison.Ordinal));

    private string PlanLineFor(WorkItem item) =>
        _logger.Records.Single(r =>
            r.Message.Contains($"work item {item.Id} (", StringComparison.Ordinal)
            && r.Message.Contains(", issued number ", StringComparison.Ordinal)
        ).Message;

    // A register row for a made-up registration number, its Org ID the number's own.
    private static PublicRegister2026Entry Row(string registrationNumber) =>
        new(null, int.Parse(registrationNumber[5..11], System.Globalization.CultureInfo.InvariantCulture), "Test Ltd");

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    // ------------------------------- gates -------------------------------

    [Fact]
    public async Task Gate_off_does_nothing()
    {
        var item = await EggerAsync();

        var result = await NewMigration(enabled: false).ApplyAsync(_persistence, Ct);

        Assert.Equal(Migration.RunResult.Disabled, result);
        Assert.Null(LastYears(await ReloadAsync(item)));
        Assert.Empty(_logger.Records);
    }

    [Fact]
    public async Task Enabled_alone_is_a_dry_run_that_writes_nothing()
    {
        var item = await EggerAsync();
        var clearfield = await ClearfieldAsync();

        var result = await RunAsync(apply: false);

        Assert.Equal(Migration.RunResult.DryRun, result);
        var reloaded = await ReloadAsync(item);
        Assert.Null(LastYears(reloaded));
        Assert.Empty(reloaded.AuditLog);
        Assert.Equal(0, reloaded.Version);
        Assert.Null(LastYears(await ReloadAsync(clearfield)));
        Assert.True(Logged(LogLevel.Information, EggerAccreditation));
        // Clearfield's issued number doesn't fit the register, so its line is a warning.
        Assert.True(Logged(LogLevel.Warning, ClearfieldAccreditation));
        Assert.False(Logged(LogLevel.Information, "APPLIED"));
    }

    [Fact]
    public async Task Apply_without_a_spot_check_name_stays_a_dry_run()
    {
        var item = await EggerAsync();

        var result = await NewMigration(confirmedBy: "  ").ApplyAsync(_persistence, Ct);

        Assert.Equal(Migration.RunResult.DryRun, result);
        Assert.Null(LastYears(await ReloadAsync(item)));
        Assert.True(Logged(LogLevel.Error, "running as a DRY RUN"));
    }

    // ------------------------------- last year's accreditation -------------------------------

    // Whoever reviews the dry run sees which organisation each line is about, and what would be written.
    [Fact]
    public async Task Dry_run_and_applied_lines_name_the_organisation()
    {
        var item = await EggerAsync(p => p["organisationName"] = "Egger (UK) Limited");

        await RunAsync();

        var plan = PlanLineFor(item);
        Assert.Contains("org 500027 Egger (UK) Limited", plan, StringComparison.Ordinal);
        Assert.Contains(EggerAccreditation, plan, StringComparison.Ordinal);
        Assert.True(Logged(LogLevel.Information, $"APPLIED: work item {item.Id} ("));
        Assert.Contains(
            _logger.Records,
            r => r.Message.StartsWith("RA-784 APPLIED", StringComparison.Ordinal)
                && r.Message.Contains("org 500027 Egger (UK) Limited", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task Apply_adds_last_years_accreditation_with_an_audit_entry()
    {
        var item = await EggerAsync();

        var result = await RunAsync();

        Assert.Equal(Migration.RunResult.Applied, result);
        var reloaded = await ReloadAsync(item);
        var lastYears = LastYears(reloaded)!;
        Assert.Equal(2026, lastYears["year"].AsInt32);
        Assert.Equal(EggerAccreditation, lastYears["accreditationNumber"].AsString);
        Assert.Equal(1, reloaded.Version);
        Assert.Equal("assessment-in-progress", reloaded.StateId);

        var entry = Assert.Single(reloaded.AuditLog);
        Assert.Equal(Migration.AddedAuditAction, entry.Action);
        Assert.Equal("Last year's accreditation number added", entry.ActionDisplayName);
        Assert.Equal("migration", entry.CreatedBy);
        Assert.Equal(s_now, entry.CreatedAt);
        Assert.Equal("2026", entry.Details["year"]);
        Assert.Equal(EggerAccreditation, entry.Details["accreditationNumber"]);
        Assert.Equal(Migration.AuditSource, entry.Details["source"]);
        Assert.Equal("RA-784", entry.Details["reason"]);
        Assert.True(Logged(LogLevel.Information, "APPLIED"));
        Assert.True(Logged(LogLevel.Information, "a.reviewer"));
    }

    [Fact]
    public async Task An_item_without_an_org_number_is_matched_on_the_registration_alone()
    {
        var item = await SeedAsync(EggerRegistration, null, "wood");

        await RunAsync();

        Assert.Equal(EggerAccreditation, LastYears(await ReloadAsync(item))!["accreditationNumber"].AsString);
    }

    [Fact]
    public async Task Only_2027_items_are_touched()
    {
        var item = await SeedAsync(EggerRegistration, 500027, "wood", accreditationYear: 2026);

        await RunAsync();

        Assert.Null(LastYears(await ReloadAsync(item)));
        Assert.Empty((await ReloadAsync(item)).AuditLog);
    }

    [Fact]
    public async Task A_registered_only_operator_gets_nothing()
    {
        var item = await SeedAsync(DtsRegistration, 500237);

        await RunAsync();

        var reloaded = await ReloadAsync(item);
        Assert.Null(LastYears(reloaded));
        Assert.Empty(reloaded.AuditLog);
        Assert.Contains(nameof(Migration.Outcome.RegisteredOnly), PlanLineFor(item), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_number_already_held_is_left_alone()
    {
        var item = await EggerAsync(p =>
            p[Migration.PayloadKey] = new BsonDocument { ["year"] = 2026, ["accreditationNumber"] = EggerAccreditation }
        );

        await RunAsync();

        var reloaded = await ReloadAsync(item);
        Assert.Empty(reloaded.AuditLog);
        Assert.Equal(0, reloaded.Version);
        Assert.Contains(nameof(Migration.Outcome.AlreadyHeld), PlanLineFor(item), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_different_number_already_held_is_reported_and_never_overwritten()
    {
        var item = await EggerAsync(p =>
            p[Migration.PayloadKey] = new BsonDocument { ["year"] = 2026, ["accreditationNumber"] = "A26ER5000270001WO" }
        );

        await RunAsync();

        var reloaded = await ReloadAsync(item);
        Assert.Equal("A26ER5000270001WO", LastYears(reloaded)!["accreditationNumber"].AsString);
        Assert.Empty(reloaded.AuditLog);
        Assert.True(Logged(LogLevel.Warning, nameof(Migration.Outcome.HoldsADifferentNumber)));
    }

    [Theory]
    [InlineData(500028, "wood")]
    [InlineData(500027, "plastic")]
    public async Task A_register_row_that_is_not_the_items_registration_is_skipped_and_reported(
        int orgNumber,
        string material
    )
    {
        var item = await SeedAsync(EggerRegistration, orgNumber, material);
        var other = await SeedAsync(DtsRegistration, 500237);
        var egger = await EggerAsync();

        var result = await RunAsync();

        Assert.Equal(Migration.RunResult.Applied, result);
        Assert.Null(LastYears(await ReloadAsync(item)));
        Assert.NotNull(LastYears(await ReloadAsync(egger)));
        Assert.Contains(nameof(Migration.Outcome.IdentityMismatch), PlanLineFor(item), StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(Migration.Outcome.IdentityMismatch), PlanLineFor(other), StringComparison.Ordinal);
    }

    // Agreed 19 November 2026: the register is used as published, even where a row looks wrong.
    [Fact]
    public async Task A_row_whose_accreditation_names_another_org_is_used_as_published()
    {
        var item = await SeedAsync(JpRecyclingRegistration, 500302);

        await RunAsync();

        Assert.Equal(JpRecyclingAccreditation, LastYears(await ReloadAsync(item))!["accreditationNumber"].AsString);
    }

    [Fact]
    public async Task A_glass_number_ending_GL_is_used_as_published()
    {
        var item = await SeedAsync(
            ComhairleRegistration,
            500541,
            "glass",
            customise: p => p["glassRecyclingProcess"] = "glass_other"
        );

        await RunAsync();

        Assert.Equal(ComhairleAccreditation, LastYears(await ReloadAsync(item))!["accreditationNumber"].AsString);
    }

    // Encirc: org 500069 holds a registration carrying 500226 (RA-764), as the register's Org ID column says.
    [Theory]
    [InlineData(500069)]
    [InlineData(500226)]
    public async Task The_org_is_matched_on_the_registration_number_or_the_registers_Org_ID(int orgNumber)
    {
        var item = await SeedAsync(EncircRegistration, orgNumber, "glass");

        await RunAsync();

        Assert.Equal(EncircAccreditation, LastYears(await ReloadAsync(item))!["accreditationNumber"].AsString);
    }

    // J. and A. Young: the register prints the number without its last letter. An item still holding that can't be
    // issued a 2027 number, so it is reported, not corrected.
    [Fact]
    public async Task J_and_A_Youngs_registration_number_missing_its_last_letter_is_reported()
    {
        var item = await SeedAsync("R26WR5000950025P", 500095);

        await RunAsync();

        var reloaded = await ReloadAsync(item);
        Assert.Null(LastYears(reloaded));
        Assert.Empty(reloaded.AuditLog);
        Assert.True(Logged(LogLevel.Warning, nameof(Migration.Outcome.RegistrationNumberIncomplete)));
        Assert.Contains("must end PL", PlanLineFor(item), StringComparison.Ordinal);
    }

    [Fact]
    public async Task J_and_A_Young_with_the_full_registration_number_is_registered_only()
    {
        var item = await SeedAsync(JAndAYoungRegistration, 500095);

        await RunAsync();

        Assert.Null(LastYears(await ReloadAsync(item)));
        Assert.Contains(nameof(Migration.Outcome.RegisteredOnly), PlanLineFor(item), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_item_not_on_the_register_is_reported()
    {
        var item = await SeedAsync("R26ER5009990001PL", 500999);

        await RunAsync();

        Assert.Null(LastYears(await ReloadAsync(item)));
        Assert.True(Logged(LogLevel.Warning, nameof(Migration.Outcome.NotOnTheRegister)));
    }

    [Fact]
    public async Task A_second_apply_changes_nothing()
    {
        var egger = await EggerAsync();
        var clearfield = await ClearfieldAsync();
        await RunAsync();
        var eggerAfter = await ReloadAsync(egger);
        var clearfieldAfter = await ReloadAsync(clearfield);

        await RunAsync();

        Assert.Equal(eggerAfter.Version, (await ReloadAsync(egger)).Version);
        Assert.Single((await ReloadAsync(egger)).AuditLog);
        Assert.Equal(clearfieldAfter.Version, (await ReloadAsync(clearfield)).Version);
        Assert.Single((await ReloadAsync(clearfield)).AuditLog);
    }

    // ------------------------------- issued numbers -------------------------------

    // RA-785 corrects wrongly issued numbers; this only reports them.
    [Fact]
    public async Task A_wrongly_issued_renewal_number_is_reported_and_left_alone()
    {
        var item = await ClearfieldAsync();

        await RunAsync();

        var reloaded = await ReloadAsync(item);
        Assert.Equal(ClearfieldIssued, ReAccreditationAccreditationNumber.Read(reloaded.Payload));
        Assert.Equal(ClearfieldAccreditation, LastYears(reloaded)!["accreditationNumber"].AsString);
        Assert.DoesNotContain(reloaded.AuditLog, e => e.Action == "accreditation-number-corrected");
        Assert.True(Logged(LogLevel.Warning, nameof(Migration.IssuedNumber.DoesNotFitTheRegister)));
    }

    [Fact]
    public async Task A_renewal_number_keeping_last_years_sequence_fits_the_register()
    {
        var item = await EggerAsync(p => p[ReAccreditationAccreditationNumber.PayloadKey] = "A27ER5000270063WO");

        await RunAsync();

        Assert.Contains(nameof(Migration.IssuedNumber.FitsTheRegister), PlanLineFor(item), StringComparison.Ordinal);
    }

    // RA-783: the material code is the application's, so a glass renewal of a GL number ends GO or GR.
    [Fact]
    public async Task A_glass_renewal_of_a_GL_number_fits_the_register()
    {
        var item = await SeedAsync(
            ComhairleRegistration,
            500541,
            "glass",
            customise: p =>
            {
                p["glassRecyclingProcess"] = "glass_other";
                p[ReAccreditationAccreditationNumber.PayloadKey] = "A27SR5005410022GO";
            }
        );

        await RunAsync();

        Assert.Contains(nameof(Migration.IssuedNumber.FitsTheRegister), PlanLineFor(item), StringComparison.Ordinal);
    }

    // RA-783: a first-time number keeps the registration's agency, process type and org, with a random sequence.
    [Theory]
    [InlineData("A27ER5002370042PL", nameof(Migration.IssuedNumber.FitsTheRegister))]
    [InlineData("A27ER5002380042PL", nameof(Migration.IssuedNumber.DoesNotFitTheRegister))]
    public async Task A_registered_only_operators_number_is_checked_against_its_registration(
        string issued,
        string expected
    )
    {
        var item = await SeedAsync(
            DtsRegistration,
            500237,
            customise: p => p[ReAccreditationAccreditationNumber.PayloadKey] = issued
        );

        await RunAsync();

        Assert.Contains(expected, PlanLineFor(item), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_summary_counts_every_outcome()
    {
        await EggerAsync();
        await SeedAsync(DtsRegistration, 500237);
        await ClearfieldAsync();

        await RunAsync(apply: false);

        Assert.Contains(
            _logger.Records,
            r =>
                r.Message.Contains("complete", StringComparison.Ordinal)
                && r.Message.Contains("work items=3", StringComparison.Ordinal)
                && r.Message.Contains("Added=2", StringComparison.Ordinal)
                && r.Message.Contains("RegisteredOnly=1", StringComparison.Ordinal)
                && r.Message.Contains("DoesNotFitTheRegister=1", StringComparison.Ordinal)
        );
    }

    // ------------------------------- edges -------------------------------

    [Fact]
    public async Task An_item_without_a_registration_number_is_reported()
    {
        var item = await SeedAsync(null, 500027);

        await RunAsync();

        Assert.Null(LastYears(await ReloadAsync(item)));
        Assert.Contains("it holds no registration number", PlanLineFor(item), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("R26ER5000270036ST", "steel", null)]
    [InlineData("R26ER5000270036WO", "Wood", null)]
    [InlineData("R26ER5000270036AL", "aluminium", null)]
    [InlineData("R26ER5000270036FB", "fibre", null)]
    [InlineData("R26ER5000270036PA", "paper", null)]
    [InlineData("R26ER5000270036PL", "plastic", null)]
    [InlineData("R26ER5000270036GR", "glass", "glass_re_melt")]
    [InlineData("R26ER5000270036GO", "glass", "glass_other")]
    [InlineData("R26ER5000270036GR", "glass", null)]
    [InlineData("R26ER5000270036GO", "glass", null)]
    public void A_material_matching_the_registration_numbers_code_is_not_a_mismatch(
        string registrationNumber,
        string material,
        string? glassRecyclingProcess
    )
    {
        var payload = new BsonDocument { ["material"] = material, ["operatorOrgNumber"] = 500027 };
        if (glassRecyclingProcess is not null)
        {
            payload["glassRecyclingProcess"] = glassRecyclingProcess;
        }

        Assert.Null(Migration.IdentityMismatch(payload, registrationNumber, Row(registrationNumber)));
    }

    [Theory]
    [InlineData("R26ER5000270036GO", "glass", "glass_re_melt")]
    [InlineData("R26ER5000270036GR", "glass", "glass_other")]
    [InlineData("R26ER5000270036PL", "concrete", null)]
    public void A_material_that_does_not_match_the_registration_numbers_code_is_a_mismatch(
        string registrationNumber,
        string material,
        string? glassRecyclingProcess
    )
    {
        var payload = new BsonDocument { ["material"] = material };
        if (glassRecyclingProcess is not null)
        {
            payload["glassRecyclingProcess"] = glassRecyclingProcess;
        }

        Assert.Contains(
            "registration number's is",
            Migration.IdentityMismatch(payload, registrationNumber, Row(registrationNumber)),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task An_item_changed_between_planning_and_writing_is_not_written()
    {
        // Loaded BEFORE configuring the substitute: an await between the call
        // and Returns() can resume on another thread, where NSubstitute no
        // longer sees the call to configure.
        var planned = await ReloadAsync(await EggerAsync());
        var racing = Substitute.For<IWorkItemPersistence>();
        racing
            .FindByPayloadFieldAsync(ReAccreditationType.Id, "accreditationYear", Arg.Any<BsonValue>(), Arg.Any<CancellationToken>())
            .Returns([planned]);
        racing
            .CorrectPayloadFieldAsync(default, default!, default!, default!, default!, Ct)
            .ReturnsForAnyArgs(false);

        var result = await NewMigration().ApplyAsync(racing, Ct);

        Assert.Equal(Migration.RunResult.Applied, result);
        Assert.True(Logged(LogLevel.Warning, "changed after planning"));
        Assert.False(Logged(LogLevel.Information, "APPLIED"));
    }

    [Fact]
    public async Task StartedAsync_applies_through_the_registered_persistence()
    {
        var item = await EggerAsync();

        await NewMigration().StartedAsync(Ct);

        Assert.Equal(EggerAccreditation, LastYears(await ReloadAsync(item))!["accreditationNumber"].AsString);
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

        await migration.StartingAsync(Ct);
        await migration.StartAsync(Ct);
        await migration.StartedAsync(Ct);
        await migration.StoppingAsync(Ct);
        await migration.StopAsync(Ct);
        await migration.StoppedAsync(Ct);

        provider.DidNotReceiveWithAnyArgs().GetService(default!);
    }
}
