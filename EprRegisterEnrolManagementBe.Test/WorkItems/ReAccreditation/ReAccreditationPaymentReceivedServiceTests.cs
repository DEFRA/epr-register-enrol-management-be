using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-523: an application queried AFTER it was duly made must move FORWARD to
/// assessment when the operator responds, not back to <c>duly-made</c>.
///
/// The guard paths matter at least as much as the happy path here, because the
/// engine keys transitions on <c>FromStateId</c> and this action shares
/// <c>updated</c> with the four <c>continue-review-during-*</c> transitions.
/// "Is the item in 'updated'?" is therefore NOT sufficient to decide it
/// applies — the origin has to be <c>duly-made</c> specifically, or an
/// application queried out of <c>submitted</c> could skip the step that anchors
/// the SLA clock.
/// </summary>
public class ReAccreditationPaymentReceivedServiceTests
{
    private const string TenantClientId = "test-client";
    private const string ActionId = "payment-received-during-duly-made";

    // ------------------------------- happy path -------------------------------

    [Fact]
    public async Task Applies_the_forward_action_for_a_duly_made_origin()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("resume-during-duly-made");

        var result = await harness.Service.RecordPaymentReceivedAsync(
            harness.WorkItem.Id, harness.User, ct);

        Assert.True(result.IsSuccess);
        await harness.Engine.Received(1).ApplyActionAsync(
            harness.WorkItem.Id, ActionId, harness.User, ct);
    }

    [Fact]
    public async Task Resolves_the_origin_from_the_most_recent_entry_that_reached_updated()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("resume-during-duly-made");

        // A stale entry from an earlier query/resume cycle, and a synthetic
        // entry that does NOT name 'updated' as its destination — neither may
        // win the origin derivation. The toStateId requirement is what stops a
        // migration's synthetic action-applied entry, stamped with the current
        // time, from mis-deriving the origin.
        harness.WorkItem.AuditLog.Insert(0, new WorkItemAuditEntry
        {
            Action = "action-applied",
            ActionDisplayName = "Action applied",
            CreatedAt = harness.WorkItem.AuditLog[0].CreatedAt.AddDays(-10),
            Details = new Dictionary<string, string?>
            {
                ["actionId"] = "resume-during-assessment",
                ["fromStateId"] = "queried",
                ["toStateId"] = "updated",
            },
        });
        harness.WorkItem.AuditLog.Add(new WorkItemAuditEntry
        {
            Action = "action-applied",
            ActionDisplayName = "Action applied",
            CreatedAt = DateTime.UtcNow,
            Details = new Dictionary<string, string?>
            {
                ["actionId"] = "resume-during-decision",
                ["fromStateId"] = "queried",
                ["toStateId"] = "duly-made",
            },
        });

        var result = await harness.Service.RecordPaymentReceivedAsync(
            harness.WorkItem.Id, harness.User, ct);

        Assert.True(result.IsSuccess);
        await harness.Engine.Received(1).ApplyActionAsync(
            harness.WorkItem.Id, ActionId, harness.User, ct);
    }

    // --------------------------- the origin guard ---------------------------

    /// <summary>
    /// The guard is POSITIVE (origin == duly-made), so every other origin is
    /// refused rather than acquiring new behaviour. 'submitted' is the one that
    /// matters most: such an application has never been duly made, so carrying
    /// it to assessment would skip payment-date capture and leave it under
    /// assessment with no SLA clock running at all.
    /// </summary>
    [Theory]
    [InlineData("resume-during-duly-making")]
    [InlineData("resume-during-assessment")]
    [InlineData("resume-during-decision")]
    public async Task Refuses_every_origin_other_than_duly_made(string resumeActionId)
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(resumeActionId);

        var result = await harness.Service.RecordPaymentReceivedAsync(
            harness.WorkItem.Id, harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        await harness.Engine.DidNotReceive().ApplyActionAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refuses_when_the_origin_cannot_be_resolved_at_all()
    {
        var ct = TestContext.Current.CancellationToken;
        // No resume entry in the audit history, so the origin is unknowable.
        // Refusing is correct: guessing could send the application forward past
        // duly making.
        var harness = new Harness(resumeActionId: null);

        var result = await harness.Service.RecordPaymentReceivedAsync(
            harness.WorkItem.Id, harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        await harness.Engine.DidNotReceive().ApplyActionAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(),
            Arg.Any<CancellationToken>());
    }

    // ------------------------ the template-version guard ------------------------

    [Fact]
    public async Task Refuses_when_the_frozen_snapshot_predates_the_transition()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("resume-during-duly-made", snapshotVersion: PreV14Snapshot());

        var result = await harness.Service.RecordPaymentReceivedAsync(
            harness.WorkItem.Id, harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        await harness.Engine.DidNotReceive().ApplyActionAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(),
            Arg.Any<CancellationToken>());
    }

    // ------------------------------ state guards ------------------------------

    [Fact]
    public async Task Already_in_assessment_is_an_idempotent_replay_not_a_conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("resume-during-duly-made", stateId: "assessment-in-progress");

        var result = await harness.Service.RecordPaymentReceivedAsync(
            harness.WorkItem.Id, harness.User, ct);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsIdempotentReplay);
        await harness.Engine.DidNotReceive().ApplyActionAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The replay set is deliberately the single target state. Widening it to
    /// continue-review's four would report success for an item that went
    /// somewhere this call could not have sent it.
    /// </summary>
    [Theory]
    [InlineData("duly-made")]
    [InlineData("queried")]
    [InlineData("awaiting-decision")]
    [InlineData("approved")]
    public async Task Any_other_state_is_a_conflict(string stateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("resume-during-duly-made", stateId: stateId);

        var result = await harness.Service.RecordPaymentReceivedAsync(
            harness.WorkItem.Id, harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
    }

    [Fact]
    public async Task Unknown_work_item_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("resume-during-duly-made", seedWorkItem: false);

        var result = await harness.Service.RecordPaymentReceivedAsync(
            harness.WorkItem.Id, harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task Work_item_of_another_type_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("resume-during-duly-made", typeId: "some-other-type");

        var result = await harness.Service.RecordPaymentReceivedAsync(
            harness.WorkItem.Id, harness.User, ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.UnknownAction, result.FailureCode);
    }

    // ------------- the two properties this whole ticket is about -------------

    /// <summary>
    /// The ticket exists because a case worker lost sight of an application.
    /// The service must not clear the assignment — and it does not, because it
    /// mutates no fields at all: it delegates to the engine, which writes state
    /// and audit only. The one place in production that nulls these four fields
    /// is <c>WorkItemService.UnassignAsync</c>.
    /// </summary>
    [Fact]
    public async Task Preserves_the_assignment_across_the_hop()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("resume-during-duly-made");
        harness.WorkItem.AssignedToId = "alice-1";
        harness.WorkItem.AssignedToName = "Alice Example";
        harness.WorkItem.AssignedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        harness.WorkItem.AssignedBy = "alice-1";

        var result = await harness.Service.RecordPaymentReceivedAsync(
            harness.WorkItem.Id, harness.User, ct);

        Assert.True(result.IsSuccess);
        Assert.Equal("alice-1", harness.WorkItem.AssignedToId);
        Assert.Equal("Alice Example", harness.WorkItem.AssignedToName);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), harness.WorkItem.AssignedAt);
        Assert.Equal("alice-1", harness.WorkItem.AssignedBy);
    }

    /// <summary>
    /// The 12-week clock is anchored to the regulator-entered payment date at
    /// duly making, not to transition time. This hop must not restart,
    /// re-anchor or extend it, and must not write a second
    /// <c>sla-clock-started</c> entry.
    /// </summary>
    [Fact]
    public async Task Leaves_the_sla_clock_byte_identical_and_writes_no_second_start_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness("resume-during-duly-made");
        var startedAt = new DateTime(2025, 11, 3, 0, 0, 0, DateTimeKind.Utc);
        harness.WorkItem.SlaClock = new WorkItemSlaClock { StartedAt = startedAt };
        var targetBefore = harness.WorkItem.SlaClock.TargetDuration;
        var auditCountBefore = harness.WorkItem.AuditLog.Count;

        var result = await harness.Service.RecordPaymentReceivedAsync(
            harness.WorkItem.Id, harness.User, ct);

        Assert.True(result.IsSuccess);
        Assert.NotNull(harness.WorkItem.SlaClock);
        Assert.Equal(startedAt, harness.WorkItem.SlaClock!.StartedAt);
        Assert.Equal(targetBefore, harness.WorkItem.SlaClock.TargetDuration);
        // The service itself appends nothing; the engine owns the single
        // action-applied entry and is substituted here.
        Assert.Equal(auditCountBefore, harness.WorkItem.AuditLog.Count);
        Assert.DoesNotContain(
            harness.WorkItem.AuditLog,
            entry => entry.Action == "sla-clock-started");
    }

    // --------------------------------- harness ---------------------------------

    /// <summary>
    /// A v13 snapshot: everything the live type declares EXCEPT the RA-523
    /// transition, which is what an item submitted before this deploy carries
    /// until the snapshot migration patches it.
    /// </summary>
    private static WorkItemTemplateSnapshot PreV14Snapshot()
    {
        var live = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        return new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v13",
            States = live.States,
            Transitions = live.Transitions.Where(t => t.ActionId != ActionId).ToList(),
        };
    }

    private sealed class Harness
    {
        public Harness(
            string? resumeActionId,
            string stateId = "updated",
            bool seedWorkItem = true,
            string typeId = ReAccreditationType.Id,
            WorkItemTemplateSnapshot? snapshotVersion = null)
        {
            WorkItem = new WorkItem
            {
                TypeId = typeId,
                StateId = stateId,
                SubmittedBy = TenantClientId,
                TemplateSnapshot =
                    snapshotVersion ?? WorkItemTemplateSnapshot.Capture(new ReAccreditationType()),
            };

            if (resumeActionId is not null)
            {
                WorkItem.AuditLog.Add(new WorkItemAuditEntry
                {
                    Action = "action-applied",
                    ActionDisplayName = "Action applied",
                    CreatedAt = DateTime.UtcNow.AddHours(-1),
                    Details = new Dictionary<string, string?>
                    {
                        ["actionId"] = resumeActionId,
                        ["fromStateId"] = "queried",
                        ["toStateId"] = "updated",
                    },
                });
            }

            Persistence = Substitute.For<IWorkItemPersistence>();
            Persistence
                .GetByIdAsync(WorkItem.Id, Arg.Any<CancellationToken>())
                .Returns(seedWorkItem ? WorkItem : null);

            Registry = Substitute.For<IWorkItemRegistry>();

            Engine = Substitute.For<IWorkItemService>();
            Engine
                .ApplyActionAsync(
                    Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(),
                    Arg.Any<CancellationToken>())
                .Returns(WorkItemActionResult.Success(WorkItem));

            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("user:id", "alice-1"),
                    new Claim("user:name", "Alice Example"),
                    new Claim("client_id", TenantClientId),
                ],
                "test"));

            Service = new ReAccreditationPaymentReceivedService(
                Persistence,
                Registry,
                Engine,
                NullLogger<ReAccreditationPaymentReceivedService>.Instance);
        }

        public WorkItem WorkItem { get; }
        public IWorkItemPersistence Persistence { get; }
        public IWorkItemRegistry Registry { get; }
        public IWorkItemService Engine { get; }
        public ClaimsPrincipal User { get; }
        public ReAccreditationPaymentReceivedService Service { get; }
    }
}
