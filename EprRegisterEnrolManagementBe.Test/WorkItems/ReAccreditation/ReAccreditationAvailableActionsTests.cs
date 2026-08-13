using EprRegisterEnrolManagementBe.Test.TestSupport;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-364: the reported bug was four identical "Resume" buttons on a work item
/// in <c>queried</c> (and, unreported but identical in kind, four "Continue
/// review" controls on one in <c>updated</c>). Both sets are declared
/// <c>CallerInvocable: false</c> — the generic action endpoint rejects them,
/// so every one of those buttons was dead on click.
///
/// These tests assert the projected action set for the real
/// <see cref="ReAccreditationType"/> rather than a synthetic type, because the
/// duplicate-DisplayName collision that produced the bug is a property of this
/// module's template specifically.
/// </summary>
public class ReAccreditationAvailableActionsTests
    : IAsyncDisposable
{
    private static readonly DateTime Now = new(2026, 4, 27, 10, 0, 0, DateTimeKind.Utc);

    private readonly TestMongoDbClientFactory _clientFactory;
    private readonly string _databaseName;
    private readonly WorkItemService _service;

    public ReAccreditationAvailableActionsTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("reaccred-actions");
        _clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _service = new WorkItemService(
            new WorkItemRegistry([new ReAccreditationType()]),
            new WorkItemPersistence(_clientFactory, NullLoggerFactory.Instance),
            NullLogger<WorkItemService>.Instance,
            // Project() is a pure read over the document and template; it never
            // reads the clock, so the real provider is fine here.
            TimeProvider.System
        );
    }

    public async ValueTask DisposeAsync() =>
        await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

    private WorkItemEngineProjection ProjectInState(string stateId) =>
        _service.Project(
            new WorkItem
            {
                Id = Guid.NewGuid(),
                TypeId = ReAccreditationType.Id,
                StateId = stateId,
                SubmittedAt = Now,
                LastModifiedAt = Now,
                SubmittedBy = "test-client",
            }
        );

    [Fact]
    public void Queried_offers_only_withdraw_and_no_duplicate_resume_buttons()
    {
        var projection = ProjectInState("queried");

        Assert.Equal(
            ["withdraw-during-query"],
            projection.AvailableActions.Select(a => a.ActionId).ToArray());

        // The actual reported symptom: four controls all labelled "Resume".
        Assert.DoesNotContain(projection.AvailableActions, a => a.DisplayName == "Resume");
        Assert.DoesNotContain(
            projection.AvailableActions,
            a => a.ActionId.StartsWith("resume-during-", StringComparison.Ordinal));
    }

    [Fact]
    public void Updated_offers_only_withdraw_and_no_duplicate_continue_review_buttons()
    {
        var projection = ProjectInState("updated");

        Assert.Equal(
            ["withdraw-during-updated"],
            projection.AvailableActions.Select(a => a.ActionId).ToArray());

        Assert.DoesNotContain(projection.AvailableActions, a => a.DisplayName == "Continue review");
        Assert.DoesNotContain(
            projection.AvailableActions,
            a => a.ActionId.StartsWith("continue-review-during-", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_projected_action_is_caller_invocable_in_every_non_terminal_state()
    {
        // Blanket guarantee behind AC1: whatever the template grows next, the
        // projection must never advertise an action the generic action
        // endpoint's CallerInvocable gate would reject.
        var type = new ReAccreditationType();

        foreach (var state in type.States.Where(s => !s.IsTerminal))
        {
            var projection = ProjectInState(state.Id);

            Assert.All(
                projection.AvailableActions,
                a => Assert.True(
                    a.CallerInvocable,
                    $"State '{state.Id}' offered non-invocable action '{a.ActionId}'."));
        }
    }

    [Fact]
    public void Terminal_states_offer_no_actions()
    {
        var type = new ReAccreditationType();

        foreach (var state in type.States.Where(s => s.IsTerminal))
        {
            Assert.Empty(ProjectInState(state.Id).AvailableActions);
        }
    }

    [Fact]
    public void Caller_invocable_state_keeps_its_ungated_actions()
    {
        // Regression guard for AC6: 'duly-made' declares no non-invocable
        // transitions, so the RA-364 filter must cost it nothing. Its
        // always-available actions stay listed even with tasks outstanding.
        var actionIds = ProjectInState("duly-made")
            .AvailableActions.Select(a => a.ActionId)
            .ToArray();

        Assert.Contains("query-during-duly-made", actionIds);
        Assert.Contains("withdraw-during-duly-made", actionIds);
    }

    [Fact]
    public void Payment_received_action_is_available_now_no_task_gate_exists()
    {
        // RA-410: this used to assert 'payment-received' was absent while its
        // task was outstanding and present once completed — the RA-364
        // CallerInvocable filter was additive to that task gate, not a
        // replacement for it. The task framework (and the gate) are gone, so
        // the caller-invocable 'payment-received' is simply always available
        // from 'duly-made' — regression cover for the ungating.
        var projection = ProjectInState("duly-made");

        Assert.Contains("payment-received", projection.AvailableActions.Select(a => a.ActionId));
        Assert.All(projection.AvailableActions, a => Assert.True(a.CallerInvocable));
    }
}
