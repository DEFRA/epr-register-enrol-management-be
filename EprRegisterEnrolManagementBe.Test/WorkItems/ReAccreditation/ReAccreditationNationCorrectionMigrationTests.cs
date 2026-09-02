using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

public class ReAccreditationNationCorrectionMigrationTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private static WorkItem BuildBrokenItem(
        Nation currentNation = Nation.England,
        string? operatorOrganisationId = "org-1",
        string? operatorRegistrationId = "reg-1") =>
        new()
        {
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            Payload = new BsonDocument
            {
                ["applicationReference"] = "RA-100000292",
                ["nation"] = currentNation.ToString(),
                ["operatorOrganisationId"] = operatorOrganisationId is null
                    ? BsonNull.Value
                    : operatorOrganisationId,
                ["operatorRegistrationId"] = operatorRegistrationId is null
                    ? BsonNull.Value
                    : operatorRegistrationId,
            },
            AuditLog =
            [
                new WorkItemAuditEntry
                {
                    Action = "routed-to-nation",
                    ActionDisplayName = "Routed to nation",
                    CreatedAt = DateTime.UtcNow,
                    Details = new Dictionary<string, string?>
                    {
                        ["nation"] = currentNation.ToString(),
                        ["derivedFrom"] = "site-address",
                    },
                },
            ],
        };

    private static WorkItem BuildAlreadyCorrectItem(Nation nation) =>
        new()
        {
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            Payload = new BsonDocument
            {
                ["nation"] = nation.ToString(),
                ["operatorOrganisationId"] = "org-1",
                ["operatorRegistrationId"] = "reg-1",
            },
            AuditLog =
            [
                new WorkItemAuditEntry
                {
                    Action = "routed-to-nation",
                    ActionDisplayName = "Routed to nation",
                    CreatedAt = DateTime.UtcNow,
                    Details = new Dictionary<string, string?>
                    {
                        ["nation"] = nation.ToString(),
                        ["derivedFrom"] = "submitted",
                    },
                },
            ],
        };

    private static IConfiguration Config(bool? enabled = true, bool? apply = true)
    {
        var values = new Dictionary<string, string?>();
        if (enabled is not null)
        {
            values[ReAccreditationNationCorrectionMigration.EnabledConfigKey] = enabled.Value.ToString();
        }
        if (apply is not null)
        {
            values[ReAccreditationNationCorrectionMigration.ApplyConfigKey] = apply.Value.ToString();
        }
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ReAccreditationNationCorrectionMigration BuildSut(
        IReExAccreditationClient reExClient, IConfiguration configuration) =>
        new(reExClient,
            configuration,
            NullLogger<ReAccreditationNationCorrectionMigration>.Instance,
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

    [Fact]
    public async Task ApplyAsync_does_nothing_when_the_feature_is_not_enabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildBrokenItem();
        var persistence = PersistenceWith(item);
        var reExClient = Substitute.For<IReExAccreditationClient>();
        var sut = BuildSut(reExClient, Config(enabled: false));

        await sut.ApplyAsync(persistence, ct);

        await persistence.DidNotReceive().QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>());
        await reExClient
            .DidNotReceive()
            .GetNationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_skips_items_not_routed_by_the_broken_derivation()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildAlreadyCorrectItem(Nation.Wales);
        var persistence = PersistenceWith(item);
        var reExClient = Substitute.For<IReExAccreditationClient>();
        var sut = BuildSut(reExClient, Config());

        await sut.ApplyAsync(persistence, ct);

        await reExClient
            .DidNotReceive()
            .GetNationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_skips_items_with_no_routed_to_nation_entry_at_all()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            Payload = new BsonDocument { ["nation"] = "England" },
        };
        var persistence = PersistenceWith(item);
        var reExClient = Substitute.For<IReExAccreditationClient>();
        var sut = BuildSut(reExClient, Config());

        await sut.ApplyAsync(persistence, ct);

        await reExClient
            .DidNotReceive()
            .GetNationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_dry_run_does_not_write_but_identifies_the_correction()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildBrokenItem(currentNation: Nation.England);
        var persistence = PersistenceWith(item);
        var reExClient = Substitute.For<IReExAccreditationClient>();
        reExClient
            .GetNationAsync("org-1", "reg-1", Arg.Any<CancellationToken>())
            .Returns(Nation.Wales);
        var sut = BuildSut(reExClient, Config(apply: false));

        await sut.ApplyAsync(persistence, ct);

        await persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
        // Payload must be untouched even in memory - a dry run's report must describe
        // the current state, not a state it already half-created.
        Assert.Equal("England", item.Payload["nation"].AsString);
    }

    [Fact]
    public async Task ApplyAsync_apply_mode_corrects_the_payload_and_records_an_audit_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildBrokenItem(currentNation: Nation.England);
        var persistence = PersistenceWith(item);
        var reExClient = Substitute.For<IReExAccreditationClient>();
        reExClient
            .GetNationAsync("org-1", "reg-1", Arg.Any<CancellationToken>())
            .Returns(Nation.Wales);
        var sut = BuildSut(reExClient, Config(apply: true));

        await sut.ApplyAsync(persistence, ct);

        Assert.Equal("Wales", item.Payload["nation"].AsString);
        await persistence.Received(1).ReplaceAsync(item, ct);

        var entry = item.AuditLog.Single(e => e.Action == "nation-corrected");
        Assert.Equal("Nation corrected", entry.ActionDisplayName);
        Assert.Equal("migration", entry.CreatedBy);
        Assert.Equal("England", entry.Details!["from"]);
        Assert.Equal("Wales", entry.Details!["to"]);
        Assert.Equal(s_now.UtcDateTime, entry.CreatedAt);

        // The original (wrong) routed-to-nation entry survives unmodified, for history.
        var original = item.AuditLog.Single(e => e.Action == "routed-to-nation");
        Assert.Equal("site-address", original.Details!["derivedFrom"]);
    }

    [Fact]
    public async Task ApplyAsync_already_correct_does_not_write()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildBrokenItem(currentNation: Nation.England);
        var persistence = PersistenceWith(item);
        var reExClient = Substitute.For<IReExAccreditationClient>();
        reExClient
            .GetNationAsync("org-1", "reg-1", Arg.Any<CancellationToken>())
            .Returns(Nation.England);
        var sut = BuildSut(reExClient, Config(apply: true));

        await sut.ApplyAsync(persistence, ct);

        await persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_skips_when_operator_identifiers_are_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildBrokenItem(operatorOrganisationId: null);
        var persistence = PersistenceWith(item);
        var reExClient = Substitute.For<IReExAccreditationClient>();
        var sut = BuildSut(reExClient, Config());

        await sut.ApplyAsync(persistence, ct);

        await reExClient
            .DidNotReceive()
            .GetNationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_skips_when_the_ReEx_lookup_fails_and_does_not_write()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildBrokenItem();
        var persistence = PersistenceWith(item);
        var reExClient = Substitute.For<IReExAccreditationClient>();
        reExClient
            .GetNationAsync("org-1", "reg-1", Arg.Any<CancellationToken>())
            .Returns((Nation?)null);
        var sut = BuildSut(reExClient, Config());

        await sut.ApplyAsync(persistence, ct);

        await persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_defaults_to_dry_run_when_ApplyConfigKey_is_absent()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildBrokenItem();
        var persistence = PersistenceWith(item);
        var reExClient = Substitute.For<IReExAccreditationClient>();
        reExClient
            .GetNationAsync("org-1", "reg-1", Arg.Any<CancellationToken>())
            .Returns(Nation.Wales);
        var sut = BuildSut(reExClient, Config(apply: null));

        await sut.ApplyAsync(persistence, ct);

        await persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }
}
