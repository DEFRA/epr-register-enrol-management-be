using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-410: an in-flight application must stop advertising the old two-step
/// decision path. Its available actions are built from its own frozen
/// snapshot, so flipping the live type alone would leave existing items
/// offering "Submit for decision" while new ones offered "Log decision".
/// </summary>
public class ReAccreditationDecisionSnapshotMigrationTests
{
    [Fact]
    public async Task It_makes_submit_for_decision_and_reject_server_resolved()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var persistence = BuildPersistence(workItem);

        await BuildMigration().ApplyAsync(persistence, ct);

        await persistence.Received(1).ReplaceAsync(workItem, ct);
        Assert.False(FindAction(workItem, "submit-for-decision").CallerInvocable);
        Assert.False(FindAction(workItem, "reject").CallerInvocable);
        Assert.Equal("v12", workItem.TemplateVersion);
        Assert.Equal("v12", workItem.TemplateSnapshot!.TemplateVersion);
    }

    /// <summary>
    /// The action ids and target states are the wire contract every stored
    /// audit entry and Notify template names. Only the invocability flag moves.
    /// </summary>
    [Fact]
    public async Task It_leaves_action_ids_and_target_states_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();

        await BuildMigration().ApplyAsync(BuildPersistence(workItem), ct);

        var reject = FindAction(workItem, "reject");
        Assert.Equal("awaiting-decision", reject.FromStateId);
        Assert.Equal("rejected", reject.ToStateId);
        Assert.Equal("Reject", reject.DisplayName);
    }

    [Fact]
    public async Task It_leaves_every_other_transition_invocable()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();

        await BuildMigration().ApplyAsync(BuildPersistence(workItem), ct);

        Assert.True(FindAction(workItem, "payment-received").CallerInvocable);
        Assert.True(FindAction(workItem, "withdraw-during-assessment").CallerInvocable);
        // Already server-resolved before RA-410 — must stay that way.
        Assert.False(FindAction(workItem, "duly-make").CallerInvocable);
    }

    [Fact]
    public async Task It_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var persistence = BuildPersistence(workItem);

        await BuildMigration().ApplyAsync(persistence, ct);
        persistence.ClearReceivedCalls();
        await BuildMigration().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }

    /// <summary>
    /// An item with no snapshot resolves its template from the live registry,
    /// so it already sees v12 rules and must not be rewritten.
    /// </summary>
    [Fact]
    public async Task It_skips_an_item_with_no_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        workItem.TemplateSnapshot = null;
        var persistence = BuildPersistence(workItem);

        await BuildMigration().ApplyAsync(persistence, ct);

        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }

    private static ReAccreditationDecisionSnapshotMigration BuildMigration() =>
        new(NullLogger<ReAccreditationDecisionSnapshotMigration>.Instance);

    private static WorkItemTransition FindAction(WorkItem workItem, string actionId) =>
        workItem.TemplateSnapshot!.Transitions.Single(t =>
            string.Equals(t.ActionId, actionId, StringComparison.OrdinalIgnoreCase)
        );

    private static WorkItem BuildWorkItem() =>
        new()
        {
            TypeId = ReAccreditationType.Id,
            StateId = "assessment-in-progress",
            SubmittedBy = "test-client",
            TemplateVersion = "v11",
            TemplateSnapshot = new WorkItemTemplateSnapshot
            {
                TemplateVersion = "v11",
                States =
                [
                    new WorkItemState("assessment-in-progress", "Updated"),
                    new WorkItemState("awaiting-decision", "Awaiting decision"),
                    new WorkItemState("rejected", "Refused", IsTerminal: true),
                ],
                Transitions =
                [
                    new WorkItemTransition(
                        "duly-make", "Duly make", "submitted", "duly-made",
                        CallerInvocable: false),
                    new WorkItemTransition(
                        "payment-received", "Payment received", "duly-made",
                        "assessment-in-progress"),
                    new WorkItemTransition(
                        "submit-for-decision", "Submit for decision",
                        "assessment-in-progress", "awaiting-decision"),
                    new WorkItemTransition(
                        "reject", "Reject", "awaiting-decision", "rejected"),
                    new WorkItemTransition(
                        "withdraw-during-assessment", "Withdraw",
                        "assessment-in-progress", "withdrawn"),
                ],
            },
        };

    private static IWorkItemPersistence BuildPersistence(WorkItem workItem)
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence
            .QueryAsync(Arg.Any<WorkItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => new WorkItemPage([workItem], 1, 1, WorkItemQuery.MaxPageSize));
        persistence
            .GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(workItem);
        return persistence;
    }
}
