using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-523: retargets <c>resume-during-duly-made</c> in every re-accreditation
/// work item's frozen snapshot from <c>updated</c> to
/// <c>assessment-in-progress</c> (v13 → v14). Mirrors
/// <see cref="ReAccreditationUpdatedStateSnapshotMigrationTests"/>'s
/// retarget-in-snapshot structure.
///
/// This is what carries the change to the items it exists for — applications
/// already sitting in <c>queried</c>, or those submitted before this deploy —
/// so a duly-made-origin resume lands decision-ready rather than on the
/// <c>updated</c> waypoint that only led back to <c>duly-made</c>.
/// </summary>
public class ReAccreditationResumeDulyMadeAssessmentSnapshotMigrationTests
{
    private const string ActionId = "resume-during-duly-made";

    private static WorkItemTransition DulyMadeResume(WorkItemTemplateSnapshot snapshot) =>
        snapshot.Transitions.Single(t => t.ActionId == ActionId);

    /// <summary>
    /// A v13 snapshot: the live template but with <c>resume-during-duly-made</c>
    /// still pointing at <c>updated</c>, i.e. what an item submitted before this
    /// deploy carries until the migration patches it.
    /// </summary>
    private static WorkItemTemplateSnapshot BuildV13Snapshot()
    {
        var snapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        return new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v13",
            States = snapshot.States,
            Transitions = snapshot
                .Transitions.Select(t =>
                    t.ActionId == ActionId ? t with { ToStateId = "updated" } : t
                )
                .ToList(),
        };
    }

    private static WorkItem BuildItem(
        string stateId = "queried",
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

    private static ReAccreditationResumeDulyMadeAssessmentSnapshotMigration BuildSut() =>
        new(NullLogger<ReAccreditationResumeDulyMadeAssessmentSnapshotMigration>.Instance);

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
    public async Task ApplyAsync_retargets_the_transition_and_bumps_the_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Equal("assessment-in-progress", DulyMadeResume(item.TemplateSnapshot!).ToStateId);
        Assert.Equal("v14", item.TemplateSnapshot!.TemplateVersion);
        Assert.Equal("v14", item.TemplateVersion);
    }

    /// <summary>
    /// Only <c>resume-during-duly-made</c> moves. The other three resume
    /// transitions must still land on <c>updated</c>.
    /// </summary>
    [Theory]
    [InlineData("resume-during-duly-making")]
    [InlineData("resume-during-assessment")]
    [InlineData("resume-during-decision")]
    public async Task ApplyAsync_leaves_the_other_resume_transitions_on_updated(string otherAction)
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        var other = item.TemplateSnapshot!.Transitions.Single(t => t.ActionId == otherAction);
        Assert.Equal("updated", other.ToStateId);
    }

    /// <summary>
    /// The legacy escape must survive: an item already parked in <c>updated</c>
    /// still needs <c>continue-review-during-duly-made</c> to move on, and
    /// <see cref="ReAccreditationUpdatedOrigin.ResolveOriginatingStateId"/>
    /// reads it to resolve those items' origin.
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
            t => t.ActionId == "continue-review-during-duly-made"
        );
        Assert.Equal("duly-made", retained.ToStateId);
    }

    /// <summary>
    /// Migrations never move a work item. An application already resumed onto
    /// <c>updated</c> under the old snapshot stays there — only a future resume
    /// changes destination.
    /// </summary>
    [Theory]
    [InlineData("queried")]
    [InlineData("updated")]
    public async Task ApplyAsync_never_changes_state(string stateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(stateId);
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Equal(stateId, item.StateId);
    }

    [Fact]
    public async Task ApplyAsync_is_idempotent_for_an_already_migrated_item()
    {
        var ct = TestContext.Current.CancellationToken;
        // Live snapshot already has resume-during-duly-made -> assessment-in-progress.
        var item = BuildItem(snapshot: WorkItemTemplateSnapshot.Capture(new ReAccreditationType()));
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        Assert.Equal("assessment-in-progress", DulyMadeResume(item.TemplateSnapshot!).ToStateId);
    }
}
