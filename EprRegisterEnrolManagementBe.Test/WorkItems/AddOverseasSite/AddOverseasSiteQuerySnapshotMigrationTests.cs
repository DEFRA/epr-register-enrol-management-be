using EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.AddOverseasSite;

/// <summary>
/// RA-698 (MBE-1): v1/v2 add-ORS snapshots are brought onto the v3 rules: the
/// query, resume and continue-review transitions present and non-invocable,
/// the withdraw* ones too, whichever migration runs first.
/// </summary>
public sealed class AddOverseasSiteQuerySnapshotMigrationTests
{
    private static bool IsBespoke(WorkItemTransition t) =>
        t.ActionId.StartsWith("query-during-", StringComparison.Ordinal)
        || t.ActionId.StartsWith("resume-during-", StringComparison.Ordinal)
        || t.ActionId.StartsWith("continue-review-during-", StringComparison.Ordinal);

    private static WorkItem BuildItem(WorkItemTemplateSnapshot snapshot) =>
        new()
        {
            TypeId = AddOverseasSiteType.Id,
            StateId = "submitted",
            AssignedToId = "bob-1",
            TemplateSnapshot = snapshot,
            TemplateVersion = snapshot.TemplateVersion,
            SubmittedAt = DateTime.UtcNow,
            LastModifiedAt = DateTime.UtcNow,
        };

    private static IWorkItemPersistence PersistenceReturning(WorkItem item, CancellationToken ct)
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence
            .QueryAsync(Arg.Any<WorkItemQuery>(), ct)
            .Returns(new WorkItemPage([item], 1, 1, WorkItemQuery.MaxPageSize));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);
        return persistence;
    }

    private static AddOverseasSiteQuerySnapshotMigration BuildSut() =>
        new(NullLogger<AddOverseasSiteQuerySnapshotMigration>.Instance);

    /// <summary>The Standard workflow as v1 snapshotted it: every transition caller-invocable by default.</summary>
    private static WorkItemTemplateSnapshot Invocable(string version) =>
        new()
        {
            TemplateVersion = version,
            States = StandardWorkflow.Definition.States,
            Transitions = StandardWorkflow
                .Definition.Transitions.Select(t => IsBespoke(t) || t.ActionId.StartsWith("withdraw", StringComparison.Ordinal) ? t with { CallerInvocable = true } : t)
                .ToList(),
        };

    [Theory]
    [InlineData("v1")]
    [InlineData("v2")]
    public async Task ApplyAsync_brings_an_older_snapshot_onto_the_v3_rules(string version)
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(Invocable(version));
        var persistence = PersistenceReturning(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);

        var snapshot = item.TemplateSnapshot!;
        Assert.Equal("v3", snapshot.TemplateVersion);
        Assert.Equal("v3", item.TemplateVersion);
        Assert.Equal(13, snapshot.Transitions.Count(IsBespoke));
        Assert.All(snapshot.Transitions.Where(IsBespoke), t => Assert.False(t.CallerInvocable));
        Assert.All(
            snapshot.Transitions.Where(t => t.ActionId.StartsWith("withdraw", StringComparison.Ordinal)),
            t => Assert.False(t.CallerInvocable)
        );
        // Exactly the live v3 rules.
        Assert.Equal(new AddOverseasSiteType().Transitions, snapshot.Transitions);

        // Only the snapshot and version move.
        Assert.Equal("submitted", item.StateId);
        Assert.Equal("bob-1", item.AssignedToId);
        await persistence.Received(1).ReplaceAsync(item, ct);
    }

    [Fact]
    public async Task ApplyAsync_adds_a_declaration_the_snapshot_lacks()
    {
        var ct = TestContext.Current.CancellationToken;
        var full = Invocable("v2");
        var snapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = full.TemplateVersion,
            States = full.States,
            Transitions = full.Transitions.Where(t => t.ActionId != "resume-during-decision").ToList(),
        };
        var item = BuildItem(snapshot);
        var persistence = PersistenceReturning(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);

        var resume = Assert.Single(item.TemplateSnapshot!.Transitions, t => t.ActionId == "resume-during-decision");
        Assert.Equal(("queried", "updated", false), (resume.FromStateId, resume.ToStateId, resume.CallerInvocable));
    }

    [Fact]
    public async Task ApplyAsync_skips_an_item_already_on_the_v3_rules()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(WorkItemTemplateSnapshot.Capture(new AddOverseasSiteType()));
        var persistence = PersistenceReturning(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().GetByIdAsync(default, ct);
        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task ApplyAsync_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(Invocable("v1"));
        var persistence = PersistenceReturning(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);
        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.Received(1).ReplaceAsync(item, ct);
    }

    [Fact]
    public async Task ApplyAsync_skips_an_item_another_instance_migrated_concurrently()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(Invocable("v2"));
        var persistence = PersistenceReturning(item, ct);
        persistence.ReplaceAsync(item, ct).ThrowsAsync(new WorkItemConcurrencyException(item.Id, 0));

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.Received(1).ReplaceAsync(item, ct);
    }

    [Fact]
    public void It_is_registered_by_the_module_after_the_v2_migration()
    {
        var services = new ServiceCollection();
        new AddOverseasSiteModule().RegisterServices(services, new ConfigurationBuilder().Build());

        var migrations = services
            .Where(d => d.ServiceType == typeof(IWorkItemMigration))
            .Select(d => d.ImplementationType)
            .ToList();
        Assert.True(
            migrations.IndexOf(typeof(AddOverseasSiteWithdrawNotCallerInvocableSnapshotMigration))
                < migrations.IndexOf(typeof(AddOverseasSiteQuerySnapshotMigration))
        );
        Assert.Contains(typeof(AddOverseasSiteQuerySnapshotMigration), migrations);
    }
}
