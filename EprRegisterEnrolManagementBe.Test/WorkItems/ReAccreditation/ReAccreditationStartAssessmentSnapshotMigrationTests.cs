using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-595 ("add" step): adds <c>start-assessment</c> to every
/// re-accreditation work item's frozen snapshot and bumps it to v15. The
/// Mongo round-trip (every other field unchanged, second run writes nothing,
/// migrated items accept the action) is covered by
/// <see cref="ReAccreditationStartAssessmentMigrationMongoIntegrationTests"/>.
/// </summary>
public class ReAccreditationStartAssessmentSnapshotMigrationTests
{
    private const string ActionId = "start-assessment";

    /// <summary>
    /// A v14 snapshot: the live template without start-assessment, plus the
    /// payment-received transition v14 still declared (v16 removed it).
    /// </summary>
    private static WorkItemTemplateSnapshot BuildV14Snapshot()
    {
        var snapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        return new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v14",
            States = snapshot.States,
            Transitions = snapshot
                .Transitions.Where(t => t.ActionId != ActionId)
                .Append(new WorkItemTransition(
                    "payment-received", "Payment received", "duly-made", "assessment-in-progress"))
                .ToList(),
        };
    }

    private static WorkItem BuildItem(WorkItemTemplateSnapshot? snapshot = null) =>
        new()
        {
            TypeId = ReAccreditationType.Id,
            StateId = "duly-made",
            TemplateSnapshot = snapshot ?? BuildV14Snapshot(),
            TemplateVersion = "v14",
            SubmittedAt = DateTime.UtcNow,
            LastModifiedAt = DateTime.UtcNow,
        };

    private static WorkItemPage SinglePage(params WorkItem[] items) =>
        new(items, items.Length, 1, WorkItemQuery.MaxPageSize);

    private static ReAccreditationStartAssessmentSnapshotMigration BuildSut() =>
        new(NullLogger<ReAccreditationStartAssessmentSnapshotMigration>.Instance);

    private static IWorkItemPersistence PersistenceReturning(WorkItem item, CancellationToken ct)
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.QueryAsync(Arg.Any<WorkItemQuery>(), ct).Returns(SinglePage(item));
        persistence.GetByIdAsync(item.Id, ct).Returns(item);
        return persistence;
    }

    [Fact]
    public async Task ApplyAsync_adds_start_assessment_and_bumps_the_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var persistence = PersistenceReturning(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);

        var added = Assert.Single(item.TemplateSnapshot!.Transitions, t => t.ActionId == ActionId);
        Assert.Equal("duly-made", added.FromStateId);
        Assert.Equal("assessment-in-progress", added.ToStateId);
        Assert.Equal("Assessment started", added.DisplayName);
        Assert.True(added.CallerInvocable);
        Assert.Equal("v15", item.TemplateSnapshot.TemplateVersion);
        Assert.Equal("v15", item.TemplateVersion);
        await persistence.Received(1).ReplaceAsync(item, ct);
    }

    [Fact]
    public async Task ApplyAsync_keeps_every_existing_transition_including_payment_received()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        var before = item.TemplateSnapshot!.Transitions.ToList();
        var persistence = PersistenceReturning(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Equal(
            before,
            item.TemplateSnapshot!.Transitions.Where(t => t.ActionId != ActionId).ToList()
        );
        Assert.Contains(item.TemplateSnapshot.Transitions, t => t.ActionId == "payment-received");
    }

    [Fact]
    public async Task ApplyAsync_skips_an_item_with_no_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem();
        item.TemplateSnapshot = null;
        var persistence = PersistenceReturning(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task ApplyAsync_is_idempotent_for_an_already_migrated_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var live = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        var item = BuildItem(live);
        item.TemplateVersion = "v15";
        var persistence = PersistenceReturning(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        Assert.Single(item.TemplateSnapshot!.Transitions, t => t.ActionId == ActionId);
    }
}
