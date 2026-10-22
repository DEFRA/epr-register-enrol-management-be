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
    public void Queried_offers_withdraw_and_sla_extend_and_no_duplicate_resume_buttons()
    {
        var projection = ProjectInState("queried");

        // RA-351: queried now also projects sla-extend (the queried self-loop)
        // so the regulator can Extend/Override the SLA of a paused application.
        Assert.Equal(
            ["sla-extend", "withdraw-during-query"],
            projection.AvailableActions.Select(a => a.ActionId).OrderBy(id => id).ToArray());

        // The actual reported symptom: four controls all labelled "Resume".
        Assert.DoesNotContain(projection.AvailableActions, a => a.DisplayName == "Resume");
        Assert.DoesNotContain(
            projection.AvailableActions,
            a => a.ActionId.StartsWith("resume-during-", StringComparison.Ordinal));
    }

    [Fact]
    public void Queried_projects_sla_extend_as_a_caller_invocable_action()
    {
        // RA-351 AC1: a queried work item must advertise sla-extend in its
        // availableActions, caller-invocable, so the generic action endpoint
        // accepts it and the FE can derive the Extend/Override SLA links.
        var projection = ProjectInState("queried");

        var slaExtend = Assert.Single(
            projection.AvailableActions.Where(a => a.ActionId == "sla-extend"));
        Assert.True(slaExtend.CallerInvocable);
        Assert.Equal("Extend SLA", slaExtend.DisplayName);
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
    public void Start_assessment_is_available_now_no_task_gate_exists_and_payment_received_is_gone()
    {
        // RA-410: the task framework (and its gate) are gone, so the
        // caller-invocable step out of 'duly-made' is simply always available
        // — regression cover for the ungating. RA-595: that step is now
        // start-assessment; v16 no longer declares payment-received.
        var projection = ProjectInState("duly-made");
        var actionIds = projection.AvailableActions.Select(a => a.ActionId).ToArray();

        Assert.Contains("start-assessment", actionIds);
        Assert.DoesNotContain("payment-received", actionIds);
        Assert.All(projection.AvailableActions, a => Assert.True(a.CallerInvocable));
    }

    [Fact]
    public void Assessment_started_is_available_from_duly_made()
    {
        // RA-595: the frontend applies start-assessment through the generic
        // action route, so it must be projected for a duly-made case.
        var startAssessment = Assert.Single(
            ProjectInState("duly-made").AvailableActions,
            a => a.ActionId == "start-assessment"
        );

        Assert.Equal("Assessment started", startAssessment.DisplayName);
        Assert.True(startAssessment.CallerInvocable);
    }

    [Theory]
    [InlineData("submitted")]
    [InlineData("assessment-in-progress")]
    [InlineData("awaiting-decision")]
    [InlineData("queried")]
    [InlineData("updated")]
    public void Assessment_started_is_not_available_outside_duly_made(string stateId)
    {
        // Positive control: the state still offers its own actions.
        var actionIds = ProjectInState(stateId).AvailableActions.Select(a => a.ActionId).ToArray();

        Assert.NotEmpty(actionIds);
        Assert.DoesNotContain("start-assessment", actionIds);
    }

    [Fact]
    public void Approved_pending_contact_offers_no_actions()
    {
        // RA-527: once a regulator has approved (step 1) the determination is
        // final. No query, withdraw, due-date change/override (sla-extend) or
        // re-determination is projected. Confirm-contact arrives in task 004.
        var projection = ProjectInState("approved-pending-contact");

        Assert.Empty(projection.AvailableActions);
        Assert.False(projection.IsTerminal);
    }

    [Fact]
    public async Task Approved_pending_contact_can_still_be_reassigned_and_unassigned()
    {
        // RA-527: someone else may need to make the applicant contact, so
        // assignment stays open while the item is not terminal.
        var ct = TestContext.Current.CancellationToken;
        var persistence = new WorkItemPersistence(_clientFactory, NullLoggerFactory.Instance);
        var type = new ReAccreditationType();
        var item = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "approved-pending-contact",
            SubmittedAt = Now,
            LastModifiedAt = Now,
            SubmittedBy = "test-client",
            AssignedToId = "alice-1",
            AssignedToName = "Alice Example",
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion,
        };
        await persistence.CreateAsync(item, ct);
        var user = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim("user:id", "alice-1"),
                    new System.Security.Claims.Claim("user:name", "Alice Example"),
                ],
                "test"
            )
        );

        var reassigned = await _service.AssignAsync(item.Id, "bob-1", "Bob Colleague", user, ct);
        Assert.True(reassigned.IsSuccess, reassigned.Message);
        Assert.Equal("bob-1", reassigned.WorkItem!.AssignedToId);

        var unassigned = await _service.UnassignAsync(item.Id, user, ct);
        Assert.True(unassigned.IsSuccess, unassigned.Message);
        Assert.Null(unassigned.WorkItem!.AssignedToId);
        Assert.Equal("approved-pending-contact", unassigned.WorkItem.StateId);
    }

    [Fact]
    public void Duly_made_still_offers_no_decision_or_sla_extend_action()
    {
        // RA-595: decisions and deadline changes stay assessment-only; the
        // frontend shows them for duly-made from its own rule.
        var actionIds = ProjectInState("duly-made").AvailableActions.Select(a => a.ActionId).ToArray();

        Assert.Contains("start-assessment", actionIds);
        Assert.DoesNotContain("sla-extend", actionIds);
        Assert.DoesNotContain("submit-for-decision", actionIds);
        Assert.DoesNotContain("reject", actionIds);
    }
}
