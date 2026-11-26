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
/// RA-495 review (v1 → v2): every <c>withdraw*</c> declaration in an add-ORS
/// work item's frozen snapshot becomes non-caller-invocable, so the generic
/// action endpoint rejects it for items submitted before v2 too. The
/// end-to-end proof (a migrated item's direct <c>/actions/withdraw*</c> call
/// is rejected) is in <c>AddOverseasSiteWithdrawEndpointTests</c>.
/// </summary>
public sealed class AddOverseasSiteWithdrawNotCallerInvocableSnapshotMigrationTests
{
    private static bool IsWithdraw(WorkItemTransition t) => t.ActionId.StartsWith("withdraw", StringComparison.Ordinal);

    /// <summary>v1: the Standard workflow as it stands, withdraw* caller-invocable.</summary>
    internal static WorkItemTemplateSnapshot BuildV1Snapshot() =>
        new()
        {
            TemplateVersion = "v1",
            States = StandardWorkflow.Definition.States,
            Transitions = StandardWorkflow.Definition.Transitions.ToList(),
        };

    private static WorkItem BuildItem(WorkItemTemplateSnapshot snapshot) =>
        new()
        {
            TypeId = AddOverseasSiteType.Id,
            StateId = "duly-made",
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

    private static AddOverseasSiteWithdrawNotCallerInvocableSnapshotMigration BuildSut() =>
        new(NullLogger<AddOverseasSiteWithdrawNotCallerInvocableSnapshotMigration>.Instance);

    [Fact]
    public async Task ApplyAsync_brings_a_v1_snapshot_onto_exactly_the_v2_rules()
    {
        var ct = TestContext.Current.CancellationToken;
        var v1 = BuildV1Snapshot();
        var item = BuildItem(v1);
        var persistence = PersistenceReturning(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);

        var snapshot = item.TemplateSnapshot!;
        Assert.Equal("v2", snapshot.TemplateVersion);
        Assert.Equal("v2", item.TemplateVersion);

        // All seven declarations flipped (withdraw-during-assessment from two states).
        var withdraws = snapshot.Transitions.Where(IsWithdraw).ToList();
        Assert.Equal(7, withdraws.Count);
        Assert.All(withdraws, t => Assert.False(t.CallerInvocable));

        // Every state and every other declaration kept, in the stored order:
        // exactly the live v2 rules.
        Assert.Equal(v1.States, snapshot.States);
        Assert.Equal(v1.Transitions.Where(t => !IsWithdraw(t)), snapshot.Transitions.Where(t => !IsWithdraw(t)));
        Assert.Equal(new AddOverseasSiteType().Transitions, snapshot.Transitions);

        // Only the snapshot and version move, and only add-ORS items are read.
        Assert.Equal("duly-made", item.StateId);
        Assert.Equal("bob-1", item.AssignedToId);
        await persistence.Received(1).ReplaceAsync(item, ct);
        await persistence
            .Received(1)
            .QueryAsync(
                Arg.Is<WorkItemQuery>(q =>
                    q.TypeIds!.SequenceEqual(new[] { AddOverseasSiteType.Id }) && q.IncludeArchived
                ),
                ct
            );
    }

    [Fact]
    public async Task ApplyAsync_skips_an_item_already_on_the_live_rules()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(WorkItemTemplateSnapshot.Capture(new AddOverseasSiteType()));
        var persistence = PersistenceReturning(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().GetByIdAsync(default, ct);
        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        Assert.Equal("v3", item.TemplateVersion);
    }

    [Fact]
    public async Task ApplyAsync_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(BuildV1Snapshot());
        var persistence = PersistenceReturning(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);
        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.Received(1).ReplaceAsync(item, ct);
    }

    [Fact]
    public async Task ApplyAsync_skips_an_item_another_instance_migrated_concurrently()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(BuildV1Snapshot());
        var persistence = PersistenceReturning(item, ct);
        persistence.ReplaceAsync(item, ct).ThrowsAsync(new WorkItemConcurrencyException(item.Id, 0));

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.Received(1).ReplaceAsync(item, ct);
    }

    [Fact]
    public void It_is_registered_by_the_add_overseas_site_module()
    {
        var services = new ServiceCollection();
        new AddOverseasSiteModule().RegisterServices(services, new ConfigurationBuilder().Build());

        Assert.Contains(
            services,
            d => d.ServiceType == typeof(IWorkItemMigration)
                && d.ImplementationType == typeof(AddOverseasSiteWithdrawNotCallerInvocableSnapshotMigration)
        );
    }
}
