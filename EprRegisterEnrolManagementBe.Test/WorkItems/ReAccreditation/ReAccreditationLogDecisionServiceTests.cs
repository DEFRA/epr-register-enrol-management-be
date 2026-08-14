using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-410: one caller-visible call carries a re-accreditation application from
/// <c>assessment-in-progress</c>, through the <c>awaiting-decision</c> hop, to
/// its terminal state — and approving still runs the bespoke approval
/// workflow rather than a bare engine transition.
/// </summary>
public class ReAccreditationLogDecisionServiceTests
{
    // --------------------------- the two-hop happy paths ---------------------------

    [Fact]
    public async Task Approving_from_assessment_applies_submit_for_decision_then_the_approval_service()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "assessment-in-progress");

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Approved,
            harness.User,
            ct
        );

        Assert.True(result.IsSuccess);
        await harness
            .Engine.Received(1)
            .ApplyActionAsync(harness.WorkItem.Id, "submit-for-decision", harness.User, ct);
        // The accreditation id, SLA clock stop, publishing job and decision
        // email all live in the approval service. Reaching 'approved' through
        // the engine instead would silently drop every one of them.
        await harness.ApprovalService.Received(1).ApproveAsync(harness.WorkItem.Id, harness.User, ct);
    }

    [Fact]
    public async Task Rejecting_from_assessment_applies_submit_for_decision_then_reject()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "assessment-in-progress");

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Rejected,
            harness.User,
            ct
        );

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["submit-for-decision", "reject"],
            harness.AppliedActionIds
        );
        await harness.ApprovalService.DidNotReceiveWithAnyArgs().ApproveAsync(default, default!, default);
    }

    /// <summary>
    /// The ordering is the point of the whole service: the operator-journey
    /// push is the gate, so it must land before the hop, which in turn lands
    /// before the outcome — never after or alongside. Pushing before anything
    /// is persisted is what makes an OJ failure a clean, no-op 500.
    /// </summary>
    [Fact]
    public async Task Oj_push_precedes_submit_for_decision_which_precedes_the_outcome()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "assessment-in-progress");

        await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Approved,
            harness.User,
            ct
        );

        Assert.Equal(["oj-push", "submit-for-decision", "approve"], harness.OrderedCalls);
    }

    // --------------------------- the operator-journey gate ---------------------------

    /// <summary>
    /// epr-p86e / RA-410: the OJ push is fired exactly once — for the final
    /// decision only, never for the internal submit-for-decision waypoint — and
    /// with the deliberate 'awaiting-decision' fromState contract-match.
    /// </summary>
    [Theory]
    [InlineData(true, "approve", "approved")]
    [InlineData(false, "reject", "rejected")]
    public async Task The_oj_push_fires_exactly_once_for_the_final_decision(
        bool approve, string expectedActionId, string expectedToStateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "assessment-in-progress");

        await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            Outcome(approve),
            harness.User,
            ct
        );

        Assert.Equal(1, harness.OrderedCalls.Count(c => c == "oj-push"));
        await harness.PushAdapter.Received(1).PushDecisionStatusChangedAsync(
            harness.WorkItem.Id, Arg.Any<Guid>(), "awaiting-decision", expectedToStateId, Arg.Any<string>(),
            expectedActionId, Arg.Any<string>(), Arg.Any<DateTime>(), ct);
    }

    /// <summary>
    /// The whole point: OJ unreachable after the retry budget means the
    /// decision is abandoned before anything is written. No hop, no approval,
    /// the item stays put, and the caller gets an UpstreamNotificationFailed
    /// the endpoint maps to a 500.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_oj_push_failure_records_no_state_change_and_maps_to_500(bool approve)
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(
            stateId: "assessment-in-progress",
            pushResult: OperatorBackendPushResult.Failure("operator journey unreachable"));

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            Outcome(approve),
            harness.User,
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.UpstreamNotificationFailed, result.FailureCode);
        Assert.Empty(harness.AppliedActionIds);
        await harness.ApprovalService.DidNotReceiveWithAnyArgs().ApproveAsync(default, default!, default);
        // A status-push-failed audit entry is still recorded for support.
        await harness.AuditAppender.Received(1).AppendAsync(
            harness.WorkItem.Id, "status-push-failed", Arg.Any<string>(),
            Arg.Any<Dictionary<string, string?>>(), harness.User, ct);
    }

    /// <summary>
    /// A disabled push (Skipped) is a pass, not a failure — a decision must
    /// still complete in an environment where the OJ push is switched off, and
    /// it is recorded under the non-alerting status-push-skipped outcome.
    /// </summary>
    [Fact]
    public async Task A_skipped_oj_push_still_completes_the_decision()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(
            stateId: "assessment-in-progress",
            pushResult: OperatorBackendPushResult.Skipped("OperatorBackendApi:Enabled is false."));

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Approved,
            harness.User,
            ct
        );

        Assert.True(result.IsSuccess);
        await harness.ApprovalService.Received(1).ApproveAsync(harness.WorkItem.Id, harness.User, ct);
        await harness.AuditAppender.Received(1).AppendAsync(
            harness.WorkItem.Id, "status-push-skipped", Arg.Any<string>(),
            Arg.Any<Dictionary<string, string?>>(), harness.User, ct);
    }

    /// <summary>
    /// On a successful decision a status-push-sent audit entry is recorded, so
    /// the single decision push is still visible in the audit trail now the
    /// post-action hook no longer fires for it.
    /// </summary>
    [Fact]
    public async Task A_successful_decision_records_a_status_push_sent_audit_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "assessment-in-progress");

        await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Approved,
            harness.User,
            ct
        );

        await harness.AuditAppender.Received(1).AppendAsync(
            harness.WorkItem.Id, "status-push-sent", "Status sent to OJ",
            Arg.Any<Dictionary<string, string?>>(), harness.User, ct);
    }

    /// <summary>
    /// The push is gated behind the same guards as the hops: a decision refused
    /// on a guard (here a conflicting terminal outcome) must not notify OJ.
    /// </summary>
    [Fact]
    public async Task A_guard_failure_does_not_notify_the_operator_journey()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "approved");

        await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Rejected,
            harness.User,
            ct
        );

        await harness.PushAdapter.DidNotReceiveWithAnyArgs().PushDecisionStatusChangedAsync(
            default, default, default!, default!, default!, default!, default!, default, default);
    }

    // --------------------------- resumability ---------------------------

    /// <summary>
    /// An application already parked in <c>awaiting-decision</c> — left there
    /// by the pre-RA-410 two-step flow, or by a failure between this service's
    /// own two hops — is finished by the identical call. This is what makes
    /// the operation safe to retry rather than needing a rescue path.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Entry_from_awaiting_decision_skips_the_hop_and_records_the_outcome(
        bool approve
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var outcome = Outcome(approve);
        var harness = new Harness(stateId: "awaiting-decision");

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            outcome,
            harness.User,
            ct
        );

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain("submit-for-decision", harness.AppliedActionIds);
    }

    /// <summary>
    /// A failed hop must write nothing and record no decision, so the caller
    /// can simply retry.
    /// </summary>
    [Fact]
    public async Task A_failed_submit_for_decision_records_no_outcome()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "assessment-in-progress");
        harness
            .Engine.ApplyActionAsync(
                Arg.Any<Guid>(),
                "submit-for-decision",
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.ConcurrencyConflict,
                    "raced"
                )
            );

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Approved,
            harness.User,
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.FailureCode);
        await harness.ApprovalService.DidNotReceiveWithAnyArgs().ApproveAsync(default, default!, default);
    }

    // --------------------------- idempotency and conflict ---------------------------

    [Theory]
    [InlineData("approved", true)]
    [InlineData("rejected", false)]
    public async Task Replaying_the_same_outcome_is_an_idempotent_no_op(
        string stateId,
        bool approve
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var outcome = Outcome(approve);
        var harness = new Harness(stateId: stateId);

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            outcome,
            harness.User,
            ct
        );

        Assert.True(result.IsSuccess);
        Assert.True(result.IsIdempotentReplay);
        Assert.Empty(harness.AppliedActionIds);
        await harness.ApprovalService.DidNotReceiveWithAnyArgs().ApproveAsync(default, default!, default);
    }

    /// <summary>
    /// The opposite outcome on an already-decided application is a conflict,
    /// never a replay and never last-write-wins. Reporting success would tell
    /// a caseworker their refusal landed on an application that is in fact
    /// approved and carrying an issued accreditation id.
    /// </summary>
    [Theory]
    [InlineData("approved", false)]
    [InlineData("rejected", true)]
    [InlineData("withdrawn", true)]
    public async Task A_conflicting_outcome_on_a_decided_application_is_refused(
        string stateId,
        bool approve
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var outcome = Outcome(approve);
        var harness = new Harness(stateId: stateId);

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            outcome,
            harness.User,
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.TerminalState, result.FailureCode);
        Assert.Empty(harness.AppliedActionIds);
        await harness.ApprovalService.DidNotReceiveWithAnyArgs().ApproveAsync(default, default!, default);
    }

    // --------------------------- guards ---------------------------

    [Theory]
    [InlineData("submitted")]
    [InlineData("duly-made")]
    [InlineData("queried")]
    [InlineData("updated")]
    public async Task A_decision_is_refused_from_any_pre_assessment_state(string stateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: stateId);

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Approved,
            harness.User,
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Empty(harness.AppliedActionIds);
    }

    [Fact]
    public async Task Returns_not_found_for_an_unknown_work_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(seedWorkItem: false);

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Approved,
            harness.User,
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task Refuses_a_work_item_of_another_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(typeId: "some-other-type");

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Approved,
            harness.User,
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.UnknownAction, result.FailureCode);
        Assert.Empty(harness.AppliedActionIds);
    }

    /// <summary>
    /// Checked before the document is even loaded, so an unauthenticated
    /// caller cannot land the first hop and then be refused the second.
    /// </summary>
    [Fact]
    public async Task Refuses_a_caller_with_no_user_id_claim_before_touching_the_work_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "assessment-in-progress", withUserId: false);

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Approved,
            harness.User,
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.MissingActorIdentity, result.FailureCode);
        Assert.Empty(harness.AppliedActionIds);
        await harness.Persistence.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
    }

    private static ReAccreditationDecisionOutcome Outcome(bool approve) =>
        approve
            ? ReAccreditationDecisionOutcome.Approved
            : ReAccreditationDecisionOutcome.Rejected;

    private sealed class Harness
    {
        public Harness(
            string stateId = "assessment-in-progress",
            bool seedWorkItem = true,
            string typeId = ReAccreditationType.Id,
            bool withUserId = true,
            OperatorBackendPushResult? pushResult = null)
        {
            WorkItem = new WorkItem
            {
                TypeId = typeId,
                StateId = stateId,
                SubmittedBy = "test-client",
            };

            Persistence = Substitute.For<IWorkItemPersistence>();
            Persistence
                .GetByIdAsync(WorkItem.Id, Arg.Any<CancellationToken>())
                .Returns(seedWorkItem ? WorkItem : null);

            // The pre-commit operator-journey gate. Default is a successful
            // push so the happy paths proceed; failure/skip paths override it.
            PushAdapter = Substitute.For<IOperatorBackendPushAdapter>();
            PushAdapter
                .PushDecisionStatusChangedAsync(
                    Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    OrderedCalls.Add("oj-push");
                    return pushResult ?? OperatorBackendPushResult.Success();
                });

            AuditAppender = Substitute.For<IWorkItemAuditAppender>();
            AuditAppender
                .AppendAsync(
                    Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<Dictionary<string, string?>>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
                .Returns(true);

            Engine = Substitute.For<IWorkItemService>();
            Engine
                .ApplyActionAsync(
                    Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var actionId = call.ArgAt<string>(1);
                    AppliedActionIds.Add(actionId);
                    OrderedCalls.Add(actionId);
                    return WorkItemActionResult.Success(WorkItem);
                });

            ApprovalService = Substitute.For<IReAccreditationApprovalService>();
            ApprovalService
                .ApproveAsync(Arg.Any<Guid>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    OrderedCalls.Add("approve");
                    return WorkItemActionResult.Success(WorkItem);
                });

            var claims = new List<Claim> { new("client_id", "test-client") };
            if (withUserId)
            {
                claims.Add(new Claim("user:id", "alice-1"));
                claims.Add(new Claim("user:name", "Alice Example"));
            }
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

            Service = new ReAccreditationLogDecisionService(
                Persistence,
                Engine,
                ApprovalService,
                PushAdapter,
                AuditAppender,
                NullLogger<ReAccreditationLogDecisionService>.Instance,
                new FakeTimeProvider());
        }

        public WorkItem WorkItem { get; }
        public IWorkItemPersistence Persistence { get; }
        public IWorkItemService Engine { get; }
        public IReAccreditationApprovalService ApprovalService { get; }
        public IOperatorBackendPushAdapter PushAdapter { get; }
        public IWorkItemAuditAppender AuditAppender { get; }
        public ClaimsPrincipal User { get; }
        public ReAccreditationLogDecisionService Service { get; }
        public List<string> AppliedActionIds { get; } = [];
        public List<string> OrderedCalls { get; } = [];
    }
}
