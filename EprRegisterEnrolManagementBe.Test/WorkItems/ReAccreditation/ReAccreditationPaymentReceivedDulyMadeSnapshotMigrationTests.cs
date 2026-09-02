using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-523: adds the <c>updated → assessment-in-progress</c>
/// <c>payment-received-during-duly-made</c> transition to every
/// re-accreditation work item's frozen snapshot (v13 → v14). Mirrors
/// <see cref="ReAccreditationSlaExtendQuerySnapshotMigrationTests"/>'s
/// structure.
///
/// This migration is what makes the story reach the items it exists for: the
/// applications already sitting in <c>updated</c> today, queried after being
/// duly made. Without it they keep only the Continue review route back to
/// <c>duly-made</c>.
/// </summary>
public class ReAccreditationPaymentReceivedDulyMadeSnapshotMigrationTests
{
    private const string ActionId = "payment-received-during-duly-made";

    private static bool IsNewTransition(WorkItemTransition t) => t.ActionId == ActionId;

    private static WorkItemTemplateSnapshot BuildV13Snapshot()
    {
        var snapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        return new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v13",
            States = snapshot.States,
            Transitions = snapshot.Transitions.Where(t => !IsNewTransition(t)).ToList(),
        };
    }

    private static WorkItem BuildItem(
        string stateId = "updated",
        WorkItemTemplateSnapshot? snapshot = null
    ) =>
        new()
        {
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            TemplateSnapshot = snapshot ?? BuildV13Snapshot(),
            TemplateVersion = "v13",
            SubmittedAt = DateTime.UtcNow,
            LastModifiedAt = DateTime.UtcNow,
        };

    private static WorkItemPage SinglePage(params WorkItem[] items) =>
        new(items, items.Length, 1, WorkItemQuery.MaxPageSize);

    private static ReAccreditationPaymentReceivedDulyMadeSnapshotMigration BuildSut() =>
        new(NullLogger<ReAccreditationPaymentReceivedDulyMadeSnapshotMigration>.Instance);

    [Fact]
    public async Task ApplyAsync_skips_an_item_with_no_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        item.TemplateSnapshot = null;
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task ApplyAsync_skips_an_item_whose_full_document_has_disappeared_by_the_time_it_is_refetched()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns((WorkItem?)null);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task ApplyAsync_adds_the_transition_and_bumps_the_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        var added = Assert.Single(item.TemplateSnapshot!.Transitions, IsNewTransition);
        Assert.Equal("updated", added.FromStateId);
        Assert.Equal("assessment-in-progress", added.ToStateId);
        Assert.Equal("v14", item.TemplateSnapshot.TemplateVersion);
        Assert.Equal("v14", item.TemplateVersion);
    }

    /// <summary>
    /// The security boundary has to survive migration, not just fresh
    /// submission. A snapshot patched with CallerInvocable: true would let a
    /// caller reach the transition through the generic action endpoint and
    /// skip duly making.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_force_sets_caller_invocable_false_on_the_added_transition()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        var added = item.TemplateSnapshot!.Transitions.Single(IsNewTransition);
        Assert.False(added.CallerInvocable);
    }

    /// <summary>
    /// Stripping continue-review-during-duly-made would make
    /// ReAccreditationUpdatedOrigin.ResolveOriginatingStateId unable to derive
    /// the origin — for exactly the items being migrated — because it resolves
    /// that transition's ToStateId out of this snapshot. The old route stops
    /// being OFFERED (a frontend concern); it must not be removed.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_retains_continue_review_during_duly_made()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        var retained = Assert.Single(
            item.TemplateSnapshot!.Transitions,
            t => t.ActionId == "continue-review-during-duly-made");
        Assert.Equal("duly-made", retained.ToStateId);
    }

    [Fact]
    public async Task ApplyAsync_preserves_existing_transitions_and_states()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var transitionsBefore = item.TemplateSnapshot!.Transitions.Count;
        var statesBefore = item.TemplateSnapshot.States.Count;
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Equal(transitionsBefore + 1, item.TemplateSnapshot!.Transitions.Count);
        Assert.Equal(statesBefore, item.TemplateSnapshot.States.Count);
    }

    /// <summary>
    /// Migrations must never move a work item. An application sitting in
    /// <c>updated</c> stays there, keeping its assignee and its SLA clock; all
    /// it gains is a forward route.
    /// </summary>
    [Theory]
    [InlineData("updated")]
    [InlineData("duly-made")]
    [InlineData("queried")]
    public async Task ApplyAsync_never_changes_state_assignment_or_sla_clock(string stateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(stateId);
        item.AssignedToId = "alice-1";
        item.AssignedToName = "Alice Example";
        var startedAt = new DateTime(2025, 11, 3, 0, 0, 0, DateTimeKind.Utc);
        item.SlaClock = new WorkItemSlaClock { StartedAt = startedAt };
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Equal(stateId, item.StateId);
        Assert.Equal("alice-1", item.AssignedToId);
        Assert.Equal("Alice Example", item.AssignedToName);
        Assert.Equal(startedAt, item.SlaClock!.StartedAt);
    }

    [Fact]
    public async Task ApplyAsync_is_idempotent_for_an_already_migrated_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(snapshot: WorkItemTemplateSnapshot.Capture(new ReAccreditationType()));
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        Assert.Single(item.TemplateSnapshot!.Transitions, IsNewTransition);
    }
}
