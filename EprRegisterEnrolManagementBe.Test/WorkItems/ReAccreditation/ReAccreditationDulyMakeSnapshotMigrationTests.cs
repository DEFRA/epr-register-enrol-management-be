using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-316 cover for the v10 → v11 snapshot migration.
///
/// This is the highest-risk part of the change. The framework matches actions
/// against a work item's OWN frozen snapshot, so if this migration misses an
/// item that item is stranded: it has no <c>duly-make</c> transition to honour
/// the new call to action, and — with the auto-transition hook deleted — nothing
/// else can move it out of <c>submitted</c> either. The property pinned here is
/// therefore "no in-flight item lacks duly-make".
///
/// RA-410: this migration used to also clear two now-deleted <c>submitted</c>
/// tasks from the snapshot as its second responsibility. The task framework —
/// and <c>WorkItemTemplateSnapshot.TasksByState</c> itself — is gone, so there
/// is nothing left to clear; a v10 snapshot's stray <c>tasksByState</c> BSON is
/// silently ignored on read like any other retired field (see
/// WorkItem's/WorkItemTemplateSnapshot's <c>[BsonIgnoreExtraElements]</c>).
/// </summary>
public class ReAccreditationDulyMakeSnapshotMigrationTests
{
    private static ReAccreditationDulyMakeSnapshotMigration BuildMigration() =>
        new(NullLogger<ReAccreditationDulyMakeSnapshotMigration>.Instance);

    /// <summary>
    /// A faithful pre-RA-316 snapshot: <c>duly-make</c> absent (stripped at v5).
    /// Built by hand rather than from the live type, which declares it under a
    /// later version.
    /// </summary>
    private static WorkItemTemplateSnapshot BuildV10Snapshot()
    {
        var live = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        return new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v10",
            States = live.States,
            Transitions = live.Transitions.Where(t => t.ActionId != "duly-make").ToList(),
        };
    }

    private static WorkItem BuildItem(
        string stateId = "submitted",
        WorkItemTemplateSnapshot? snapshot = null
    ) =>
        new()
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

    // ------------------------- one bad document must not stop the batch -------------------------

    /// <summary>
    /// epr-dtkw. A snapshot stored without `tasksByState` deserialises with the
    /// property null, and `NeedsMigration` reads it via `GetTasksForState`. That
    /// throw used to escape `ApplyAsync` entirely: the host logged "failed;
    /// continuing startup. Will retry on next boot", the next boot met the same
    /// document, and every item behind it stayed unmigrated forever — no
    /// `duly-make` transition, so duly making refused them.
    ///
    /// Built by deserialising a snapshot document rather than by object
    /// initialiser, because `required` stops C# constructing the broken shape;
    /// only the BSON deserialiser can produce it.
    /// </summary>
    [Fact]
    public async Task A_snapshot_with_no_tasksByState_does_not_abandon_the_batch()
    {
        var ct = TestContext.Current.CancellationToken;
        var poisoned = BuildItem(snapshot: SnapshotWithNullTasksByState());
        var healthy = BuildItem();
        var persistence = BuildPersistence(poisoned, healthy);

        // Must not throw — the whole point.
        await BuildMigration().ApplyAsync(persistence, ct);

        // And the document AFTER the bad one is migrated, not skipped.
        Assert.Contains(healthy.TemplateSnapshot!.Transitions, t => t.ActionId == "duly-make");
        Assert.Equal("v11", healthy.TemplateVersion);
    }

    /// <summary>
    /// epr-dtkw, review follow-up. The poisoned document is not merely survived
    /// — it is the one that MOST needs migrating (null TasksByState AND no
    /// duly-make transition), so it must actually be migrated, not skipped into
    /// the `failed` bucket where it keeps failing every boot and duly making
    /// keeps refusing it. `PatchSnapshot` dereferences `TasksByState` directly,
    /// so without a fallback there it throws ArgumentNullException, the batch's
    /// broad catch swallows it, and this document never completes.
    /// </summary>
    [Fact]
    public async Task A_snapshot_with_no_tasksByState_is_itself_migrated()
    {
        var ct = TestContext.Current.CancellationToken;
        var poisoned = BuildItem(snapshot: SnapshotWithNullTasksByState());
        var persistence = BuildPersistence(poisoned);

        await BuildMigration().ApplyAsync(persistence, ct);

        Assert.Contains(poisoned.TemplateSnapshot!.Transitions, t => t.ActionId == "duly-make");
        Assert.Equal("v11", poisoned.TemplateVersion);
        // RA-410 removed TasksByState, so the epr-dtkw assertions that followed
        // (task list rebuilt, GetTasksForState no longer throwing) have nothing
        // left to check. What still matters — and is what this case was really
        // guarding — is that a snapshot missing the element migrates at all
        // rather than throwing and stalling the batch on every boot.
    }

    /// <summary>
    /// A document that throws for some other reason is skipped too, and the run
    /// still completes. The catch is deliberately broad: narrowing it to the
    /// exception we happened to see would just move the stall to the next
    /// unanticipated shape.
    /// </summary>
    [Fact]
    public async Task A_document_that_cannot_be_loaded_is_skipped_not_fatal()
    {
        var ct = TestContext.Current.CancellationToken;
        var broken = BuildItem();
        var healthy = BuildItem();
        var persistence = BuildPersistence(broken, healthy);
        persistence
            .GetByIdAsync(broken.Id, Arg.Any<CancellationToken>())
            .Returns<Task<WorkItem?>>(_ => throw new InvalidOperationException("unreadable"));

        await BuildMigration().ApplyAsync(persistence, ct);

        Assert.Contains(healthy.TemplateSnapshot!.Transitions, t => t.ActionId == "duly-make");
    }

    /// <summary>
    /// Cancellation is not a document problem — a shutdown mid-migration must
    /// propagate rather than be logged as a skipped work item.
    /// </summary>
    [Fact]
    public async Task Cancellation_still_propagates()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var item = BuildItem();
        var persistence = BuildPersistence(item);
        persistence
            .GetByIdAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns<Task<WorkItem?>>(_ => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BuildMigration().ApplyAsync(persistence, cancelled.Token)
        );
    }

    /// <summary>
    /// The pre-RA-316 snapshot shape as stored, minus `tasksByState`.
    /// </summary>
    private static WorkItemTemplateSnapshot SnapshotWithNullTasksByState()
    {
        var document = BuildV10Snapshot().ToBsonDocument();
        document.Remove("TasksByState");
        document.Remove("tasksByState");
        return BsonSerializer.Deserialize<WorkItemTemplateSnapshot>(document);
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

        // management-fe resolves its detail template by this version and falls
        // back to a generic template — silently — if it is unrecognised. Both
        // fields are restamped.
        Assert.Equal("v11", item.TemplateVersion);
        Assert.Equal("v11", item.TemplateSnapshot.TemplateVersion);

        await persistence.Received(1).ReplaceAsync(item, ct);
    }

    // RA-410: It_clears_the_deleted_submitted_state_tasks is gone —
    // WorkItemTemplateSnapshot.TasksByState no longer exists, so there is
    // nothing left for this migration to clear (see the class doc).

    [Fact]
    public async Task It_leaves_every_other_transition_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var before = item.TemplateSnapshot!.Transitions.Select(t => t.ActionId).ToHashSet();
        var persistence = BuildPersistence(item);

        await BuildMigration().ApplyAsync(persistence, ct);

        // Nothing removed: the new set is the old set plus duly-make.
        var after = item.TemplateSnapshot.Transitions.Select(t => t.ActionId).ToHashSet();
        Assert.Empty(before.Except(after));
        Assert.Equal(["duly-make"], after.Except(before));
        // payment-received in particular survives — 16 e2e specs drive it.
        Assert.Contains("payment-received", after);
    }

    // ------------------------------- safety rails -------------------------------

    /// <summary>
    /// An item sitting in <c>submitted</c> is NOT auto-advanced. Duly making
    /// needs a payment date only the regulator can supply, and inventing one
    /// would anchor the 12-week SLA to a fiction. Such items get the call to
    /// action like any other, which is where they belong.
    /// </summary>
    [Fact]
    public async Task It_does_not_auto_advance_a_submitted_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = BuildPersistence(item);

        await BuildMigration().ApplyAsync(persistence, ct);

        Assert.Equal("submitted", item.StateId);
        Assert.Null(item.SlaClock);
        Assert.Empty(item.AuditLog);
    }

    // RA-410: It_preserves_recorded_completions_of_the_deleted_tasks is gone —
    // WorkItem.CompletedTaskIdsByState / TaskStatusesByState no longer exist,
    // so there is nothing left for a recorded completion to live in.

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

    // RA-410: It_finishes_a_half_applied_item is gone. Its "half applied"
    // scenario depended on two independent conditions — duly-make presence
    // and the deleted tasks' presence — that NeedsMigration used to check
    // separately. With TasksByState gone there is only one condition left
    // (duly-make presence), so a "half applied" state is no longer
    // constructible; It_is_idempotent already covers the one-condition case.

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
