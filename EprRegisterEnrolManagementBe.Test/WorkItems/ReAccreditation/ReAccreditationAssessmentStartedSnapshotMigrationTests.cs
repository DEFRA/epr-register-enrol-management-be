using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-527 follow-up (v17 → v18): adds the <c>assessment-started</c> state,
/// retargets <c>start-assessment</c> to it and adds its four exits to every
/// re-accreditation work item's frozen snapshot. The Mongo round-trip (every
/// other stored field unchanged, a second run writes nothing, a migrated Duly
/// made case starts into <c>assessment-started</c> and every exit works) is
/// covered by <see cref="ReAccreditationAssessmentStartedMigrationMongoIntegrationTests"/>.
/// </summary>
public class ReAccreditationAssessmentStartedSnapshotMigrationTests
{
    private const string StartedStateId = "assessment-started";

    /// <summary>
    /// v17: the live (v18) template without 'assessment-started', with
    /// start-assessment still landing on 'assessment-in-progress'.
    /// </summary>
    internal static WorkItemTemplateSnapshot BuildV17Snapshot()
    {
        var live = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        return new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v17",
            States = live.States.Where(s => s.Id != StartedStateId).ToList(),
            Transitions = live
                .Transitions.Where(t => t.FromStateId != StartedStateId)
                .Select(t =>
                    t.ActionId == "start-assessment" ? t with { ToStateId = "assessment-in-progress" } : t)
                .ToList(),
        };
    }

    private static WorkItem BuildItem(WorkItemTemplateSnapshot? snapshot, string stateId = "duly-made") =>
        new()
        {
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            AssignedToId = "bob-1",
            TemplateSnapshot = snapshot,
            TemplateVersion = snapshot?.TemplateVersion ?? "v17",
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

    private static ReAccreditationAssessmentStartedSnapshotMigration BuildSut() =>
        new(NullLogger<ReAccreditationAssessmentStartedSnapshotMigration>.Instance);

    private static List<WorkItemTransition> Sorted(IEnumerable<WorkItemTransition> transitions) =>
        transitions.OrderBy(t => t.ActionId).ThenBy(t => t.FromStateId).ToList();

    [Fact]
    public async Task ApplyAsync_brings_a_v17_snapshot_onto_exactly_the_v18_rules()
    {
        var ct = TestContext.Current.CancellationToken;
        var v17 = BuildV17Snapshot();
        var item = BuildItem(v17);
        var persistence = PersistenceReturning(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);

        var snapshot = item.TemplateSnapshot!;
        Assert.Equal("v18", snapshot.TemplateVersion);
        Assert.Equal("v18", item.TemplateVersion);

        // The new state, declared straight after duly-made.
        var states = snapshot.States.ToList();
        var started = Assert.Single(states, s => s.Id == StartedStateId);
        Assert.Equal("Duly made", started.DisplayName);
        Assert.False(started.IsTerminal);
        Assert.Equal(states.FindIndex(s => s.Id == "duly-made") + 1, states.IndexOf(started));
        Assert.Equal(v17.States, states.Where(s => s.Id != StartedStateId).ToList());

        // start-assessment retargeted, keeping its id, label and invocability.
        var start = Assert.Single(snapshot.Transitions, t => t.ActionId == "start-assessment");
        Assert.Equal(
            new WorkItemTransition("start-assessment", "Assessment started", "duly-made", StartedStateId),
            start);

        // The four exits, with the live type's display names and invocability.
        var exits = snapshot.Transitions.Where(t => t.FromStateId == StartedStateId);
        Assert.Equal(
            Sorted(
            [
                new("submit-for-decision", "Submit for decision", StartedStateId, "awaiting-decision",
                    CallerInvocable: false),
                new("query-during-assessment", "Query", StartedStateId, "queried"),
                new("withdraw-during-assessment", "Withdraw", StartedStateId, "withdrawn"),
                new("sla-extend", "Extend SLA", StartedStateId, "assessment-in-progress",
                    CallerInvocable: false),
            ]),
            Sorted(exits));

        // Nothing else changed.
        Assert.Equal(
            Sorted(v17.Transitions.Where(t => t.ActionId != "start-assessment")),
            Sorted(snapshot.Transitions.Where(t =>
                t.ActionId != "start-assessment" && t.FromStateId != StartedStateId)));

        // Exactly the live v18 rules.
        var live = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        Assert.Equal(live.States, snapshot.States);
        Assert.Equal(Sorted(live.Transitions), Sorted(snapshot.Transitions));
        await persistence.Received(1).ReplaceAsync(item, ct);
    }

    [Theory]
    [InlineData("duly-made")]
    [InlineData("assessment-in-progress")]
    [InlineData("queried")]
    public async Task ApplyAsync_never_moves_or_reassigns_the_case(string stateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(BuildV17Snapshot(), stateId);
        var persistence = PersistenceReturning(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Equal(stateId, item.StateId);
        Assert.Equal("bob-1", item.AssignedToId);
        Assert.Equal("v18", item.TemplateVersion);
    }

    [Fact]
    public async Task ApplyAsync_skips_an_item_already_on_the_v18_rules()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(WorkItemTemplateSnapshot.Capture(new ReAccreditationType()));
        var persistence = PersistenceReturning(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task ApplyAsync_skips_an_item_with_no_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = BuildItem(snapshot: null);
        var persistence = PersistenceReturning(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        Assert.Equal("v17", item.TemplateVersion);
    }

    /// <summary>
    /// v18 grew in place while unreleased (RA-527 follow-up tasks 002, 005,
    /// 006 and 007), so an item snapshotted by an earlier build carries part
    /// of v18. It gains only what it lacks; what it has stays where it is.
    /// </summary>
    [Theory]
    [InlineData("task-002", new[] { "sla-extend", "query-during-assessment", "withdraw-during-assessment" })]
    [InlineData("task-005", new[] { "query-during-assessment", "withdraw-during-assessment" })]
    [InlineData("task-006", new[] { "withdraw-during-assessment" })]
    public async Task ApplyAsync_gives_a_partially_migrated_snapshot_only_what_it_lacks(
        string build,
        string[] missingExits)
    {
        _ = build;
        var ct = TestContext.Current.CancellationToken;
        var live = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        var partial = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v18",
            States = live.States,
            Transitions = live
                .Transitions.Where(t =>
                    !(t.FromStateId == StartedStateId && missingExits.Contains(t.ActionId)))
                .ToList(),
        };
        var item = BuildItem(partial, StartedStateId);
        var persistence = PersistenceReturning(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);

        var snapshot = item.TemplateSnapshot!;
        Assert.Equal(live.States, snapshot.States);
        // What it had is untouched and in its original order; the rest is appended.
        Assert.Equal(partial.Transitions, snapshot.Transitions.Take(partial.Transitions.Count).ToList());
        Assert.Equal(
            missingExits.Order().ToList(),
            snapshot.Transitions.Skip(partial.Transitions.Count).Select(t => t.ActionId).Order().ToList());
        Assert.Equal(Sorted(live.Transitions), Sorted(snapshot.Transitions));
        Assert.Equal(StartedStateId, item.StateId);
        await persistence.Received(1).ReplaceAsync(item, ct);
    }

    [Fact]
    public async Task ApplyAsync_replaces_a_differing_declaration_in_place_rather_than_duplicating_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var live = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        // An earlier shape: the state under another label, and a caller-invocable sla-extend.
        var differing = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v18",
            States = live
                .States.Select(s => s.Id == StartedStateId ? s with { DisplayName = "Assessment started" } : s)
                .ToList(),
            Transitions = live
                .Transitions.Select(t =>
                    t.ActionId == "sla-extend" && t.FromStateId == StartedStateId
                        ? t with { CallerInvocable = true }
                        : t)
                .ToList(),
        };
        var item = BuildItem(differing, StartedStateId);
        var persistence = PersistenceReturning(item, ct);

        await BuildSut().ApplyAsync(persistence, ct);

        Assert.Equal(live.States, item.TemplateSnapshot!.States);
        Assert.Equal(live.Transitions, item.TemplateSnapshot.Transitions);
        await persistence.Received(1).ReplaceAsync(item, ct);
    }
}
