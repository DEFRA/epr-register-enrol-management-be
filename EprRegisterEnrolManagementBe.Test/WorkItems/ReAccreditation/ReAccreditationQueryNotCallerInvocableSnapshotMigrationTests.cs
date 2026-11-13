using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-000 (v18 → v19): every <c>query-during-*</c> declaration in a work
/// item's frozen snapshot becomes non-caller-invocable, so the generic action
/// endpoint rejects it for items submitted before v19 too. The end-to-end
/// proof (a migrated item's direct <c>/actions/query-during-*</c> call is
/// rejected) is in <c>ReAccreditationEndpointTests</c>.
/// </summary>
public class ReAccreditationQueryNotCallerInvocableSnapshotMigrationTests
{
    private static readonly string[] s_queryActionIds =
    [
        "query-during-duly-making",
        "query-during-duly-made",
        "query-during-assessment",
        "query-during-decision",
    ];

    private static bool IsQuery(WorkItemTransition t) => s_queryActionIds.Contains(t.ActionId);

    /// <summary>
    /// v18: the live (v19) template with every query-during-* declaration
    /// still caller-invocable.
    /// </summary>
    internal static WorkItemTemplateSnapshot BuildV18Snapshot()
    {
        var live = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        return new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v18",
            States = live.States,
            Transitions = live
                .Transitions.Select(t => IsQuery(t) ? t with { CallerInvocable = true } : t)
                .ToList(),
        };
    }

    private static WorkItem BuildItem(WorkItemTemplateSnapshot snapshot, string stateId = "duly-made") =>
        new()
        {
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
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

    private static ReAccreditationQueryNotCallerInvocableSnapshotMigration BuildSut() =>
        new(NullLogger<ReAccreditationQueryNotCallerInvocableSnapshotMigration>.Instance);

    private static List<WorkItemTransition> Sorted(IEnumerable<WorkItemTransition> transitions) =>
        transitions.OrderBy(t => t.ActionId).ThenBy(t => t.FromStateId).ToList();

    [Fact]
    public async Task ApplyAsync_brings_a_v18_snapshot_onto_exactly_the_v19_rules()
    {
        var ct = TestContext.Current.CancellationToken;
        var v18 = BuildV18Snapshot();
        var item = BuildItem(v18);
        var persistence = PersistenceReturning(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);

        var snapshot = item.TemplateSnapshot!;
        Assert.Equal("v19", snapshot.TemplateVersion);
        Assert.Equal("v19", item.TemplateVersion);

        // All five declarations flipped, nothing else about them changed.
        var queries = snapshot.Transitions.Where(IsQuery).ToList();
        Assert.Equal(5, queries.Count);
        Assert.All(queries, t => Assert.False(t.CallerInvocable));
        Assert.Equal(
            Sorted(v18.Transitions.Where(IsQuery).Select(t => t with { CallerInvocable = false })),
            Sorted(queries));
        Assert.Contains(queries, t => t.ActionId == "query-during-assessment" && t.FromStateId == "assessment-started");

        // Every other declaration and every state kept, in the stored order.
        Assert.Equal(v18.States, snapshot.States);
        Assert.Equal(v18.Transitions.Where(t => !IsQuery(t)), snapshot.Transitions.Where(t => !IsQuery(t)));

        // Exactly the live v19 rules.
        var live = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        Assert.Equal(Sorted(live.Transitions), Sorted(snapshot.Transitions));

        // Only the snapshot and version move.
        Assert.Equal("duly-made", item.StateId);
        Assert.Equal("bob-1", item.AssignedToId);
        await persistence.Received(1).ReplaceAsync(item, ct);
    }

    [Fact]
    public async Task ApplyAsync_skips_an_item_already_on_the_v19_rules()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(WorkItemTemplateSnapshot.Capture(new ReAccreditationType()));
        var persistence = PersistenceReturning(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task ApplyAsync_flips_an_older_snapshot_that_has_only_some_query_transitions()
    {
        // A v6-era snapshot: only the two pre-assessment query transitions.
        var ct = TestContext.Current.CancellationToken;
        var v18 = BuildV18Snapshot();
        var old = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v6",
            States = v18.States,
            Transitions = v18
                .Transitions.Where(t => !IsQuery(t) || t.ActionId is "query-during-duly-making" or "query-during-duly-made")
                .ToList(),
        };
        var item = BuildItem(old, stateId: "submitted");
        var persistence = PersistenceReturning(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);

        var queries = item.TemplateSnapshot!.Transitions.Where(IsQuery).ToList();
        Assert.Equal(2, queries.Count);
        Assert.All(queries, t => Assert.False(t.CallerInvocable));
        Assert.Equal("v19", item.TemplateVersion);
    }

    [Fact]
    public async Task The_v18_migration_leaves_a_v19_item_alone_so_the_two_never_undo_each_other()
    {
        // Regression guard: comparing on full equality, the v18 migration saw
        // the v19 query-during-assessment (CallerInvocable: false) as missing,
        // re-patched it back to v18, and this migration then flipped it again
        // on every start-up.
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(WorkItemTemplateSnapshot.Capture(new ReAccreditationType()));
        var persistence = PersistenceReturning(item, ct);

        await new ReAccreditationAssessmentStartedSnapshotMigration(
                NullLogger<ReAccreditationAssessmentStartedSnapshotMigration>.Instance)
            .ApplyAsync(persistence, ct);
        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        Assert.Equal("v19", item.TemplateVersion);
    }

    [Fact]
    public async Task A_v17_item_run_through_the_v18_then_v19_migrations_ends_on_the_v19_rules()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(ReAccreditationAssessmentStartedSnapshotMigrationTests.BuildV17Snapshot());
        var persistence = PersistenceReturning(item, ct);

        await new ReAccreditationAssessmentStartedSnapshotMigration(
                NullLogger<ReAccreditationAssessmentStartedSnapshotMigration>.Instance)
            .ApplyAsync(persistence, ct);
        await BuildSut().ApplyAsync(persistence, ct);

        var live = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        Assert.Equal("v19", item.TemplateVersion);
        Assert.Equal(live.States, item.TemplateSnapshot!.States);
        Assert.Equal(Sorted(live.Transitions), Sorted(item.TemplateSnapshot.Transitions));
    }

    [Fact]
    public void It_is_registered_last_of_the_snapshot_migrations_so_it_sets_the_final_version()
    {
        var services = new ServiceCollection();
        new ReAccreditationModule().RegisterServices(services, new ConfigurationBuilder().Build());

        var snapshotMigrations = services
            .Where(d => d.ServiceType == typeof(IWorkItemMigration))
            .Select(d => d.ImplementationType)
            .Where(t => t is not null && typeof(ReAccreditationSnapshotMigrationBase).IsAssignableFrom(t))
            .ToList();

        Assert.Equal(typeof(ReAccreditationAssessmentStartedSnapshotMigration), snapshotMigrations[^2]);
        Assert.Equal(typeof(ReAccreditationQueryNotCallerInvocableSnapshotMigration), snapshotMigrations[^1]);
    }
}
