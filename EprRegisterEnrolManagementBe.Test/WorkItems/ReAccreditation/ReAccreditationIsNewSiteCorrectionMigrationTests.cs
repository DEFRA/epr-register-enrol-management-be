using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// epr-2uxy remediation gates. The failure mode here is asymmetric and that
/// asymmetry is the whole reason these tests exist: the defect being fixed errs
/// toward over-showing (a spurious "New" badge is visible and traceable), while
/// a bad correction errs toward under-showing (a fabricated <c>false</c> hides a
/// genuinely new site from the regulator, silently). So every gate is tested for
/// its refusal, not just its happy path.
/// </summary>
public class ReAccreditationIsNewSiteCorrectionMigrationTests
{
    private static readonly DateTime s_inWindow =
        new(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTimeOffset s_now =
        new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    private static BsonDocument ReExSite(bool isNewSite, string siteName = "ReEx Site") => new()
    {
        ["siteName"] = siteName,
        ["siteAddress"] = "1 ReEx Way",
        ["country"] = "Netherlands",
        ["isEu"] = true,
        ["isOecd"] = true,
        ["isNewSite"] = isNewSite
    };

    private static WorkItem BuildItem(DateTime? submittedAt = null, params BsonDocument[] sites) =>
        new()
        {
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            SubmittedAt = submittedAt ?? s_inWindow,
            Payload = new BsonDocument
            {
                ["applicationReference"] = "RA-100000292",
                ["overseasSites"] = new BsonDocument { ["sites"] = new BsonArray(sites) }
            }
        };

    private static IConfiguration Config(
        bool? enabled = true, string? confirmedBy = "tom.halley", bool? apply = true)
    {
        var values = new Dictionary<string, string?>();
        if (enabled is not null)
        {
            values[ReAccreditationIsNewSiteCorrectionMigration.EnabledConfigKey] =
                enabled.Value.ToString();
        }

        if (confirmedBy is not null)
        {
            values[ReAccreditationIsNewSiteCorrectionMigration.SpotCheckConfirmedByConfigKey] =
                confirmedBy;
        }

        if (apply is not null)
        {
            values[ReAccreditationIsNewSiteCorrectionMigration.ApplyConfigKey] =
                apply.Value.ToString();
        }

        // Pin the upper window bound so the tests do not drift with wall time.
        values[ReAccreditationIsNewSiteAudit.DeployedAtConfigKey] = "2026-08-15T00:00:00Z";

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ReAccreditationIsNewSiteCorrectionMigration BuildSut(IConfiguration configuration) =>
        new(configuration,
            NullLogger<ReAccreditationIsNewSiteCorrectionMigration>.Instance,
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

    private static bool IsNewSiteOf(WorkItem item, int index) =>
        item.Payload["overseasSites"]["sites"][index]["isNewSite"].AsBoolean;

    // ── Gate 1: off by default ───────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_does_nothing_when_the_feature_is_not_enabled()
    {
        // Registered in DI unconditionally, so "absent configuration" is the
        // state it is in for every environment that has not opted in. It must
        // not even read.
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(sites: ReExSite(isNewSite: true));
        var persistence = PersistenceWith(item);

        await BuildSut(Config(enabled: null)).ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().QueryAsync(default!, default);
        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
        Assert.True(IsNewSiteOf(item, 0));
    }

    // ── Gate 2: the spot-check ───────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ApplyAsync_refuses_to_run_without_a_recorded_spot_check(string? confirmedBy)
    {
        // The lead's ruling: constructible is not safe-to-run-unreviewed. The
        // orsId discriminator being sound is not sufficient authority — someone
        // has to have confirmed it against real records first, and their name is
        // the evidence that happened.
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(sites: ReExSite(isNewSite: true));
        var persistence = PersistenceWith(item);

        await BuildSut(Config(confirmedBy: confirmedBy)).ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
        Assert.True(IsNewSiteOf(item, 0));
    }

    // ── Gate 3: dry run ──────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_defaults_to_dry_run_and_writes_nothing()
    {
        // Enabled and spot-checked, but apply not set. The dry run is the more
        // valuable artefact in the short term — it doubles as the count — so it
        // must be the default rather than something you opt into.
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(sites: ReExSite(isNewSite: true));
        var persistence = PersistenceWith(item);

        await BuildSut(Config(apply: null)).ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
        Assert.True(IsNewSiteOf(item, 0));
        Assert.DoesNotContain(item.AuditLog, e =>
            e.Action == ReAccreditationIsNewSiteCorrectionMigration.AuditAction);
    }

    // ── Gate 4: per-site verdict ─────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_corrects_a_provably_corrupt_site_when_every_gate_is_satisfied()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(sites: ReExSite(isNewSite: true));
        var persistence = PersistenceWith(item);

        await BuildSut(Config()).ApplyAsync(persistence, ct);

        Assert.False(IsNewSiteOf(item, 0));
        await persistence.Received(1).ReplaceAsync(item, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_refuses_a_site_whose_orsId_is_missing_but_carries_operator_detail()
    {
        // The safety assertion the lead asked to be encoded in code rather than
        // left for a human to notice. orsId was historically client-clobberable,
        // so this shape may be an operator-added site with its orsId stripped —
        // correcting it would hide a genuinely new site.
        var ct = TestContext.Current.CancellationToken;
        var ambiguous = ReExSite(isNewSite: true, siteName: "Stripped");
        ambiguous["operationCode"] = "R3";
        var item = BuildItem(sites: ambiguous);
        var persistence = PersistenceWith(item);

        await BuildSut(Config()).ApplyAsync(persistence, ct);

        Assert.True(IsNewSiteOf(item, 0));
        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }

    [Fact]
    public async Task ApplyAsync_leaves_an_operator_added_site_alone()
    {
        // orsId present means the operator really did add it, so isNewSite: true
        // is the genuine value and must survive untouched.
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(sites: new BsonDocument
        {
            ["orsId"] = "ORS-2026-0292",
            ["siteName"] = "Operator Site",
            ["isNewSite"] = true
        });
        var persistence = PersistenceWith(item);

        await BuildSut(Config()).ApplyAsync(persistence, ct);

        Assert.True(IsNewSiteOf(item, 0));
        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }

    [Fact]
    public async Task ApplyAsync_corrects_only_the_corrupt_site_on_a_mixed_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var ambiguous = ReExSite(isNewSite: true, siteName: "Stripped");
        ambiguous["contactName"] = "Someone";

        var item = BuildItem(
            null,
            ReExSite(isNewSite: true, siteName: "Corrupt"),
            ambiguous,
            new BsonDocument
            {
                ["orsId"] = "ORS-1",
                ["siteName"] = "Operator",
                ["isNewSite"] = true
            });
        var persistence = PersistenceWith(item);

        await BuildSut(Config()).ApplyAsync(persistence, ct);

        Assert.False(IsNewSiteOf(item, 0));  // corrected
        Assert.True(IsNewSiteOf(item, 1));   // ambiguous, refused
        Assert.True(IsNewSiteOf(item, 2));   // operator-added, correct
        await persistence.Received(1).ReplaceAsync(item, Arg.Any<CancellationToken>());
    }

    // ── Window bound ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-07-01T00:00:00Z")]  // before isNewSite was ever transmitted
    [InlineData("2026-09-01T00:00:00Z")]  // after the RA-292 deploy
    public async Task ApplyAsync_ignores_items_outside_the_window(string submittedAt)
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(
            DateTime.Parse(submittedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal |
                System.Globalization.DateTimeStyles.AssumeUniversal),
            ReExSite(isNewSite: true));
        var persistence = PersistenceWith(item);

        await BuildSut(Config()).ApplyAsync(persistence, ct);

        Assert.True(IsNewSiteOf(item, 0));
        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }

    // ── Audit trail ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_records_who_authorised_the_correction_on_the_audit_log()
    {
        // A correction that silently rewrites regulator-visible data must leave
        // a trace naming the person whose spot-check authorised it — otherwise
        // the audit trail says a value changed but not on whose authority.
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(sites: ReExSite(isNewSite: true, siteName: "ReEx Site"));
        var persistence = PersistenceWith(item);

        await BuildSut(Config(confirmedBy: "tom.halley")).ApplyAsync(persistence, ct);

        var entry = Assert.Single(item.AuditLog, e =>
            e.Action == ReAccreditationIsNewSiteCorrectionMigration.AuditAction);
        Assert.Equal("migration", entry.CreatedBy);
        Assert.Equal(s_now.UtcDateTime, entry.CreatedAt);
        Assert.Equal("epr-2uxy", entry.Details["issue"]);
        Assert.Equal("tom.halley", entry.Details["spotCheckConfirmedBy"]);
        Assert.Contains("ReEx Site", entry.Details["sites"]);
    }

    // ── Idempotency ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_is_idempotent()
    {
        // Migrations run on every boot, so the second pass must be a no-op. A
        // corrected site reads false and therefore classifies as NotFlaggedNew.
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(sites: ReExSite(isNewSite: true));
        var persistence = PersistenceWith(item);
        var sut = BuildSut(Config());

        await sut.ApplyAsync(persistence, ct);
        await sut.ApplyAsync(persistence, ct);

        Assert.False(IsNewSiteOf(item, 0));
        await persistence.Received(1).ReplaceAsync(item, Arg.Any<CancellationToken>());
        Assert.Single(item.AuditLog, e =>
            e.Action == ReAccreditationIsNewSiteCorrectionMigration.AuditAction);
    }

    [Fact]
    public async Task ApplyAsync_tolerates_an_item_with_no_overseas_sites()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            SubmittedAt = s_inWindow,
            Payload = new BsonDocument { ["organisationName"] = "No Sites Ltd" }
        };
        var persistence = PersistenceWith(item);

        await BuildSut(Config()).ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }

    [Fact]
    public async Task ApplyAsync_tolerates_a_non_document_entry_in_the_sites_array()
    {
        // Payloads are schemaless, so the sites array can legitimately contain
        // anything. A malformed entry must be stepped over rather than abort the
        // run and leave the rest of the environment uncorrected.
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(sites: ReExSite(isNewSite: true));
        item.Payload["overseasSites"]["sites"].AsBsonArray.Add("not-a-document");
        var persistence = PersistenceWith(item);

        await BuildSut(Config()).ApplyAsync(persistence, ct);

        Assert.False(IsNewSiteOf(item, 0));
    }

    [Fact]
    public async Task ApplyAsync_labels_a_site_with_no_name_by_its_index()
    {
        // siteName is optional. Falling back to the index keeps the audit entry
        // and the dry-run report actionable — "site[2]" still identifies which
        // element changed.
        var ct = TestContext.Current.CancellationToken;
        var unnamed = ReExSite(isNewSite: true);
        unnamed.Remove("siteName");
        var item = BuildItem(sites: unnamed);
        var persistence = PersistenceWith(item);

        await BuildSut(Config()).ApplyAsync(persistence, ct);

        var entry = Assert.Single(item.AuditLog, e =>
            e.Action == ReAccreditationIsNewSiteCorrectionMigration.AuditAction);
        Assert.Contains("site[0]", entry.Details["sites"]);
    }

    [Fact]
    public async Task ApplyAsync_refuses_an_unnamed_ambiguous_site_by_index()
    {
        var ct = TestContext.Current.CancellationToken;
        var unnamed = ReExSite(isNewSite: true);
        unnamed.Remove("siteName");
        unnamed["contactName"] = "Someone";
        var item = BuildItem(sites: unnamed);
        var persistence = PersistenceWith(item);

        await BuildSut(Config()).ApplyAsync(persistence, ct);

        Assert.True(IsNewSiteOf(item, 0));
        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }

    [Fact]
    public async Task ApplyAsync_pages_through_every_work_item()
    {
        // A full page back means there may be more. Stopping after page one
        // would silently leave most of a real environment uncorrected while
        // reporting success.
        var ct = TestContext.Current.CancellationToken;
        var firstPage = Enumerable
            .Range(0, WorkItemQuery.MaxPageSize)
            .Select(_ => BuildItem(sites: ReExSite(isNewSite: true)))
            .ToArray();
        var secondPage = BuildItem(sites: ReExSite(isNewSite: true));

        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence
            .QueryAsync(Arg.Is<WorkItemQuery>(q => q.Page == 1), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(
                firstPage, firstPage.Length + 1, 1, WorkItemQuery.MaxPageSize));
        persistence
            .QueryAsync(Arg.Is<WorkItemQuery>(q => q.Page == 2), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage(
                [secondPage], firstPage.Length + 1, 2, WorkItemQuery.MaxPageSize));
        foreach (var item in firstPage.Append(secondPage))
        {
            persistence.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        }

        await BuildSut(Config()).ApplyAsync(persistence, ct);

        Assert.False(IsNewSiteOf(secondPage, 0));
        await persistence.Received(1).ReplaceAsync(secondPage, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_uses_the_system_clock_when_no_time_provider_is_supplied()
    {
        // The production DI registration supplies no TimeProvider, so the
        // default-argument path is the one that actually ships.
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(sites: ReExSite(isNewSite: true));
        var persistence = PersistenceWith(item);
        var sut = new ReAccreditationIsNewSiteCorrectionMigration(
            Config(), NullLogger<ReAccreditationIsNewSiteCorrectionMigration>.Instance);

        await sut.ApplyAsync(persistence, ct);

        var entry = Assert.Single(item.AuditLog, e =>
            e.Action == ReAccreditationIsNewSiteCorrectionMigration.AuditAction);
        Assert.NotEqual(default, entry.CreatedAt);
    }

    [Fact]
    public async Task ApplyAsync_defaults_the_window_end_to_now_when_no_deploy_time_is_configured()
    {
        // The deploy time is optional, and omitting it is the likely first run.
        // Defaulting to now over-reports rather than under-reports — the safe
        // direction — but it must still actually bound and still correct.
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(sites: ReExSite(isNewSite: true));
        var persistence = PersistenceWith(item);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ReAccreditationIsNewSiteCorrectionMigration.EnabledConfigKey] = "true",
                [ReAccreditationIsNewSiteCorrectionMigration.SpotCheckConfirmedByConfigKey] =
                    "tom.halley",
                [ReAccreditationIsNewSiteCorrectionMigration.ApplyConfigKey] = "true"
            })
            .Build();

        await BuildSut(configuration).ApplyAsync(persistence, ct);

        Assert.False(IsNewSiteOf(item, 0));
    }

    [Fact]
    public void Name_identifies_the_issue_it_remediates()
    {
        // Logged at the start of every run; a future maintainer seeing it in a
        // boot log needs the issue id to find this runbook.
        Assert.Contains("epr-2uxy", BuildSut(Config()).Name);
    }

    [Fact]
    public async Task ApplyAsync_skips_an_item_that_cannot_be_re_read()
    {
        // QueryAsync returns projections; the full document is fetched before
        // mutating. A disappearing item must not throw and abort the run.
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(sites: ReExSite(isNewSite: true));
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence
            .QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemPage([item], 1, 1, WorkItemQuery.MaxPageSize));
        persistence.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        await BuildSut(Config()).ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }
}
