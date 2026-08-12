using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-316 cover for the v10 → v11 snapshot migration.
///
/// This is the highest-risk part of the change. The framework matches actions
/// against a work item's OWN frozen snapshot, so if this migration misses an
/// item that item is stranded: it has no <c>duly-make</c> transition to honour
/// the new call to action, and — with the auto-transition hook deleted — nothing
/// else can move it out of <c>submitted</c> either. The two properties pinned
/// here are therefore "no in-flight item keeps the deleted tasks" and "no
/// in-flight item lacks duly-make".
/// </summary>
public class ReAccreditationDulyMakeSnapshotMigrationTests
{
    private static ReAccreditationDulyMakeSnapshotMigration BuildMigration() =>
        new(NullLogger<ReAccreditationDulyMakeSnapshotMigration>.Instance);

    /// <summary>
    /// A faithful pre-RA-316 snapshot: <c>duly-make</c> absent (stripped at v5)
    /// and the two <c>submitted</c> tasks present. Built by hand rather than
    /// from the live type, which no longer declares either.
    /// </summary>
    private static WorkItemTemplateSnapshot BuildV10Snapshot()
    {
        var live = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        var tasksByState = new Dictionary<string, List<WorkItemTask>>(
            live.TasksByState,
            StringComparer.OrdinalIgnoreCase
        )
        {
            ["submitted"] =
            [
                new WorkItemTask("verify-organisation-details", "Verify organisation details"),
                new WorkItemTask(
                    "confirm-application-completeness",
                    "Confirm application is duly made"
                ),
            ],
        };

        return new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v10",
            States = live.States,
            Transitions = live.Transitions.Where(t => t.ActionId != "duly-make").ToList(),
            TasksByState = tasksByState,
        };
    }

    private static WorkItem BuildItem(
        string stateId = "submitted",
        WorkItemTemplateSnapshot? snapshot = null,
        bool tasksTicked = false
    )
    {
        var item = new WorkItem
        {
            Id = Guid.NewGuid(),
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            SubmittedAt = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc),
            LastModifiedAt = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc),
            SubmittedBy = "test-client",
            TemplateSnapshot = snapshot ?? BuildV10Snapshot(),
            TemplateVersion = "v10",
            Payload = new BsonDocument { ["organisationName"] = "Acme Ltd" },
        };

        if (tasksTicked)
        {
            item.CompletedTaskIdsByState["submitted"] = new(
                ["verify-organisation-details", "confirm-application-completeness"],
                StringComparer.OrdinalIgnoreCase
            );
            item.TaskStatusesByState["submitted"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["verify-organisation-details"] = WorkItemTaskStatus.Completed,
                ["confirm-application-completeness"] = WorkItemTaskStatus.Completed,
            };
        }

        return item;
    }

    private static IWorkItemPersistence BuildPersistence(params WorkItem[] items)
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

    // ------------------------- the two stranding properties -------------------------

    [Fact]
    public async Task It_adds_the_duly_make_transition_and_restamps_the_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = BuildPersistence(item);

        await BuildMigration().ApplyAsync(persistence, ct);

        var transition = Assert.Single(
            item.TemplateSnapshot!.Transitions,
            t => t.ActionId == "duly-make"
        );
        Assert.Equal("submitted", transition.FromStateId);
        Assert.Equal("duly-made", transition.ToStateId);
        // Must match the live declaration exactly, or a migrated item would be
        // judged by different rules than a freshly submitted one.
        Assert.False(transition.CallerInvocable);
        Assert.False(transition.RequiresAllTasksComplete);

        // management-fe resolves its detail template by this version and falls
        // back to a generic template — silently — if it is unrecognised. Both
        // fields are restamped.
        Assert.Equal("v11", item.TemplateVersion);
        Assert.Equal("v11", item.TemplateSnapshot.TemplateVersion);

        await persistence.Received(1).ReplaceAsync(item, ct);
    }

    [Fact]
    public async Task It_clears_the_deleted_submitted_state_tasks()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = BuildPersistence(item);

        await BuildMigration().ApplyAsync(persistence, ct);

        Assert.Empty(item.TemplateSnapshot!.GetTasksForState("submitted"));
        // The key is kept (empty) rather than removed — 'submitted' is still a
        // declared state, it just has no tasks.
        Assert.True(item.TemplateSnapshot.TasksByState.ContainsKey("submitted"));
    }

    [Fact]
    public async Task It_leaves_every_other_states_tasks_and_transitions_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var before = item.TemplateSnapshot!.Transitions.Select(t => t.ActionId).ToHashSet();
        var persistence = BuildPersistence(item);

        await BuildMigration().ApplyAsync(persistence, ct);

        Assert.Equal(
            ["confirm-registration-fee-paid"],
            item.TemplateSnapshot!.GetTasksForState("duly-made").Select(t => t.Id)
        );
        Assert.Equal(3, item.TemplateSnapshot.GetTasksForState("assessment-in-progress").Count);

        // Nothing removed: the new set is the old set plus duly-make.
        var after = item.TemplateSnapshot.Transitions.Select(t => t.ActionId).ToHashSet();
        Assert.Empty(before.Except(after));
        Assert.Equal(["duly-make"], after.Except(before));
        // payment-received in particular survives — 16 e2e specs drive it.
        Assert.Contains("payment-received", after);
    }

    // ------------------------------- safety rails -------------------------------

    /// <summary>
    /// An item sitting in <c>submitted</c> with both former tasks ticked is NOT
    /// auto-advanced. Duly making needs a payment date only the regulator can
    /// supply, and inventing one would anchor the 12-week SLA to a fiction. Such
    /// items get the call to action like any other, which is where they belong.
    /// </summary>
    [Fact]
    public async Task It_does_not_auto_advance_an_item_whose_old_tasks_were_all_ticked()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(tasksTicked: true);
        var persistence = BuildPersistence(item);

        await BuildMigration().ApplyAsync(persistence, ct);

        Assert.Equal("submitted", item.StateId);
        Assert.Null(item.SlaClock);
        Assert.Empty(item.AuditLog);
    }

    /// <summary>
    /// Recorded completions of the deleted tasks are history — a record of work
    /// a regulator actually did. They are inert once the snapshot declares no
    /// tasks for the state, so there is nothing to gain by destroying them.
    /// </summary>
    [Fact]
    public async Task It_preserves_recorded_completions_of_the_deleted_tasks()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(tasksTicked: true);
        var persistence = BuildPersistence(item);

        await BuildMigration().ApplyAsync(persistence, ct);

        Assert.Contains("verify-organisation-details", item.CompletedTaskIdsByState["submitted"]);
        Assert.Equal(
            WorkItemTaskStatus.Completed,
            item.TaskStatusesByState["submitted"]["verify-organisation-details"]
        );
    }

    [Fact]
    public async Task It_migrates_items_in_every_state_including_terminal_ones()
    {
        var ct = TestContext.Current.CancellationToken;
        var items = new[]
        {
            BuildItem("submitted"),
            BuildItem("updated"),
            BuildItem("queried"),
            BuildItem("duly-made"),
            BuildItem("approved"),
            BuildItem("withdrawn"),
        };
        var persistence = BuildPersistence(items);

        await BuildMigration().ApplyAsync(persistence, ct);

        Assert.All(
            items,
            item =>
            {
                Assert.Equal("v11", item.TemplateVersion);
                Assert.Contains(item.TemplateSnapshot!.Transitions, t => t.ActionId == "duly-make");
                Assert.Empty(item.TemplateSnapshot.GetTasksForState("submitted"));
            }
        );
    }

    [Fact]
    public async Task It_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = BuildPersistence(item);

        await BuildMigration().ApplyAsync(persistence, ct);
        await BuildMigration().ApplyAsync(persistence, ct);

        // Second pass is a no-op: still exactly one write, and no duplicate
        // transition.
        await persistence.Received(1).ReplaceAsync(item, ct);
        Assert.Single(item.TemplateSnapshot!.Transitions, t => t.ActionId == "duly-make");
    }

    /// <summary>
    /// A half-applied item — one property already true, the other not — is still
    /// picked up and finished off, because the two conditions are tested
    /// independently rather than by trusting the stored version string.
    /// </summary>
    [Fact]
    public async Task It_finishes_a_half_applied_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var live = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        // duly-make present (so a version check alone would call this done) but
        // the deleted tasks still there.
        var halfApplied = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v11",
            States = live.States,
            Transitions = live.Transitions,
            TasksByState = new Dictionary<string, List<WorkItemTask>>(
                live.TasksByState,
                StringComparer.OrdinalIgnoreCase
            )
            {
                ["submitted"] = [new WorkItemTask("verify-organisation-details", "Verify")],
            },
        };
        var item = BuildItem(snapshot: halfApplied);
        var persistence = BuildPersistence(item);

        await BuildMigration().ApplyAsync(persistence, ct);

        Assert.Empty(item.TemplateSnapshot!.GetTasksForState("submitted"));
        await persistence.Received(1).ReplaceAsync(item, ct);
    }

    /// <summary>
    /// An item with no snapshot at all resolves its template from the live
    /// registry, so it already sees v11 and must not be touched.
    /// </summary>
    [Fact]
    public async Task It_skips_items_with_no_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        item.TemplateSnapshot = null;
        var persistence = BuildPersistence(item);

        await BuildMigration().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }

    [Fact]
    public async Task A_concurrency_conflict_is_swallowed_so_the_run_continues()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = BuildItem();
        var second = BuildItem();
        var persistence = BuildPersistence(first, second);
        persistence
            .ReplaceAsync(first, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException(new WorkItemConcurrencyException(first.Id, expectedVersion: 0))
            );

        await BuildMigration().ApplyAsync(persistence, ct);

        // The second item is still migrated despite the first one conflicting.
        Assert.Equal("v11", second.TemplateVersion);
    }

    /// <summary>
    /// The trap RA-316 had to defuse. <c>ReAccreditationDulyMadeSnapshotMigration</c>
    /// STRIPS duly-make as its v4 → v5 step and is registered first, so before
    /// RA-316 gated it on the item's version the two would have fought on every
    /// boot: strip, re-add, strip, re-add — two pointless writes per item per
    /// start-up and a window in which no item could be duly made. Running both
    /// in registration order, twice, must be stable.
    /// </summary>
    [Fact]
    public async Task The_v4_strip_migration_and_this_one_do_not_fight()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = BuildPersistence(item);

        var strip = new ReAccreditationDulyMadeSnapshotMigration(
            NullLogger<ReAccreditationDulyMadeSnapshotMigration>.Instance
        );
        var reinstate = BuildMigration();

        // Two full boots' worth of the registered migration order.
        await strip.ApplyAsync(persistence, ct);
        await reinstate.ApplyAsync(persistence, ct);
        await strip.ApplyAsync(persistence, ct);
        await reinstate.ApplyAsync(persistence, ct);

        Assert.Equal("v11", item.TemplateVersion);
        Assert.Single(item.TemplateSnapshot!.Transitions, t => t.ActionId == "duly-make");
        // Exactly one write across both boots — the second boot is a genuine
        // no-op, not a re-application.
        await persistence.Received(1).ReplaceAsync(item, ct);
    }
}
