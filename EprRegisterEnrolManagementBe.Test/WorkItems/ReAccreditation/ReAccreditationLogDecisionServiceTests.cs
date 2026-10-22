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
/// its decided state.
///
/// RA-527: an Approved determination is step 1 of two. It mints the number
/// and lands in <c>approved-pending-contact</c> through the approval service,
/// with no operator-journey push. A Refused determination is still single-step
/// (task 005 changes it): pre-commit push, hop, reject.
/// </summary>
public class ReAccreditationLogDecisionServiceTests
{
    [Fact]
    public void Constructor_defaults_time_provider_when_omitted()
    {
        // Covers the `timeProvider ?? TimeProvider.System` branch, which the
        // Harness below never exercises because it always supplies a
        // FakeTimeProvider explicitly. The assignment runs at construction,
        // so merely building the service exercises the branch.
        var service = new ReAccreditationLogDecisionService(
            Substitute.For<IWorkItemPersistence>(),
            Substitute.For<IWorkItemService>(),
            Substitute.For<IReAccreditationApprovalService>(),
            Substitute.For<IOperatorBackendPushAdapter>(),
            Substitute.For<IWorkItemAuditAppender>(),
            NullLogger<ReAccreditationLogDecisionService>.Instance
        );

        Assert.NotNull(service);
    }

    // --------------------------- the two-hop happy paths ---------------------------

    /// <summary>
    /// RA-527: approving records the determination (step 1) through the
    /// approval service, which stamps the number and moves the item to
    /// approved-pending-contact. It never applies the outcome: ApproveAsync
    /// (SLA stop, publishing, Decision email) is step 2's job.
    /// </summary>
    [Fact]
    public async Task Approving_from_assessment_applies_submit_for_decision_then_records_the_step_one_approval()
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
        Assert.Equal(["submit-for-decision"], harness.AppliedActionIds);
        await harness
            .ApprovalService.Received(1)
            .RecordApprovedDeterminationAsync(
                harness.WorkItem.Id,
                harness.User,
                Arg.Any<AccreditationNumberResult>(),
                ct
            );
        await harness
            .ApprovalService.DidNotReceiveWithAnyArgs()
            .ApproveAsync(default, default!, ct);
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
        Assert.Equal(["submit-for-decision", "reject"], harness.AppliedActionIds);
        await harness
            .ApprovalService.DidNotReceiveWithAnyArgs()
            .ApproveAsync(default, default!, ct);
        await harness
            .ApprovalService.DidNotReceiveWithAnyArgs()
            .RecordApprovedDeterminationAsync(default, default!, default!, ct);
    }

    /// <summary>
    /// RA-527: an approval mints its number before anything is written, then
    /// takes the hop, then records the determination. There is no
    /// operator-journey push at step 1 at all.
    /// </summary>
    [Fact]
    public async Task Approving_resolves_the_number_then_takes_the_hop_then_records_the_determination()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "assessment-in-progress");

        await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Approved,
            harness.User,
            ct
        );

        Assert.Equal(
            ["resolve-number", "submit-for-decision", "record-approved-determination"],
            harness.OrderedCalls
        );
    }

    /// <summary>
    /// A refusal is still applied in one step: the push is the gate and must
    /// land before the hop, which in turn lands before the outcome.
    /// </summary>
    [Fact]
    public async Task Rejecting_pushes_before_submit_for_decision_which_precedes_the_outcome()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "assessment-in-progress");

        await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Rejected,
            harness.User,
            ct
        );

        Assert.Equal(
            ["registration-accreditation-push", "submit-for-decision", "reject"],
            harness.OrderedCalls
        );
    }

    /// <summary>
    /// epr-r9oy: Reject never mints a number, so resolving one for a Reject
    /// outcome would be pure waste — and worse, would call the backend's
    /// accreditation-number endpoint for an application that will never be
    /// approved.
    /// </summary>
    [Fact]
    public async Task Rejecting_never_resolves_an_accreditation_number()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "assessment-in-progress");

        await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Rejected,
            harness.User,
            ct
        );

        await harness
            .ApprovalService.DidNotReceiveWithAnyArgs()
            .ResolveAccreditationNumberAsync(default, default, ct);
    }

    /// <summary>
    /// The number minted before anything is written is the SAME instance
    /// handed to the step 1 write — the approval service must not mint a
    /// second one.
    /// </summary>
    [Fact]
    public async Task The_number_resolved_up_front_is_forwarded_unchanged_to_the_step_one_write()
    {
        var ct = TestContext.Current.CancellationToken;
        var expected = AccreditationNumberResult.Success("A25ER5000270036WO");
        var harness = new Harness(stateId: "assessment-in-progress", numberResult: expected);

        await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Approved,
            harness.User,
            ct
        );

        Assert.Same(expected, harness.LastRecordedNumberArgument);
        await harness
            .ApprovalService.Received(1)
            .ResolveAccreditationNumberAsync(harness.WorkItem.Id, Arg.Any<Guid>(), ct);
    }

    /// <summary>
    /// A failed mint abandons the determination before anything is written,
    /// so the caller can simply retry.
    /// </summary>
    [Fact]
    public async Task A_number_resolution_failure_abandons_the_decision_before_anything_is_written()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(
            stateId: "assessment-in-progress",
            numberResult: AccreditationNumberResult.Failure("backend unreachable")
        );

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Approved,
            harness.User,
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.AccreditationNumberUnavailable, result.FailureCode);
        Assert.Empty(harness.AppliedActionIds);
        await harness
            .ApprovalService.DidNotReceiveWithAnyArgs()
            .RecordApprovedDeterminationAsync(default, default!, default!, ct);
    }

    // --------------------------- the operator-journey gate ---------------------------

    /// <summary>
    /// RA-527 step 1: the operator must not learn of an approval before the
    /// regulator has contacted them, so an Approved determination is never
    /// pushed — and leaves no status-push audit entry behind either.
    /// </summary>
    [Fact]
    public async Task Approving_never_notifies_the_operator_journey()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "assessment-in-progress");

        await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Approved,
            harness.User,
            ct
        );

        await harness
            .PushAdapter.DidNotReceiveWithAnyArgs()
            .PushDecisionStatusChangedAsync(
                default,
                default,
                default!,
                default!,
                default!,
                default!,
                default!,
                default,
                ct
            );
        await harness
            .AuditAppender.DidNotReceiveWithAnyArgs()
            .AppendAsync(default, default!, default!, default!, default!, ct);
    }

    /// <summary>
    /// epr-p86e / RA-410: for a refusal the Registration & Accreditation
    /// service push is fired exactly once — for the final decision only,
    /// never for the internal submit-for-decision waypoint — and with the
    /// deliberate 'awaiting-decision' fromState contract-match.
    /// </summary>
    [Fact]
    public async Task The_registration_accreditation_push_fires_exactly_once_for_a_refusal()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "assessment-in-progress");

        await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Rejected,
            harness.User,
            ct
        );

        Assert.Equal(1, harness.OrderedCalls.Count(c => c == "registration-accreditation-push"));
        await harness
            .PushAdapter.Received(1)
            .PushDecisionStatusChangedAsync(
                harness.WorkItem.Id,
                Arg.Any<Guid>(),
                "awaiting-decision",
                "rejected",
                Arg.Any<string>(),
                "reject",
                Arg.Any<string>(),
                Arg.Any<DateTime>(),
                ct
            );
    }

    /// <summary>
    /// The Registration & Accreditation service unreachable after the retry
    /// budget means the refusal is abandoned before anything is written. No
    /// hop, the item stays put, and the caller gets an
    /// UpstreamNotificationFailed the endpoint maps to a 500.
    /// </summary>
    [Fact]
    public async Task A_registration_accreditation_push_failure_records_no_state_change_and_maps_to_500()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(
            stateId: "assessment-in-progress",
            pushResult: OperatorBackendPushResult.Failure("operator journey unreachable")
        );

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Rejected,
            harness.User,
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.UpstreamNotificationFailed, result.FailureCode);
        Assert.Empty(harness.AppliedActionIds);
        // A status-push-failed audit entry is still recorded for support.
        await harness
            .AuditAppender.Received(1)
            .AppendAsync(
                harness.WorkItem.Id,
                "status-push-failed",
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(),
                harness.User,
                ct
            );
    }

    /// <summary>
    /// A disabled push (Skipped) is a pass, not a failure — a refusal must
    /// still complete in an environment where the Registration & Accreditation
    /// service push is switched off, and it is recorded under the non-alerting
    /// status-push-skipped outcome.
    /// </summary>
    [Fact]
    public async Task A_skipped_registration_accreditation_push_still_completes_the_decision()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(
            stateId: "assessment-in-progress",
            pushResult: OperatorBackendPushResult.Skipped("OperatorBackendApi:Enabled is false.")
        );

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Rejected,
            harness.User,
            ct
        );

        Assert.True(result.IsSuccess);
        Assert.Equal(["submit-for-decision", "reject"], harness.AppliedActionIds);
        await harness
            .AuditAppender.Received(1)
            .AppendAsync(
                harness.WorkItem.Id,
                "status-push-skipped",
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(),
                harness.User,
                ct
            );
    }

    /// <summary>
    /// On a successful refusal a status-push-sent audit entry is recorded, so
    /// the single decision push is still visible in the audit trail now the
    /// post-action hook no longer fires for it.
    /// </summary>
    [Fact]
    public async Task A_successful_refusal_records_a_status_push_sent_audit_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "assessment-in-progress");

        await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Rejected,
            harness.User,
            ct
        );

        await harness
            .AuditAppender.Received(1)
            .AppendAsync(
                harness.WorkItem.Id,
                "status-push-sent",
                "Status sent to the Registration & Accreditation service",
                Arg.Any<Dictionary<string, string?>>(),
                harness.User,
                ct
            );
    }

    [Fact]
    public async Task A_refusal_still_succeeds_when_the_status_push_audit_entry_fails_to_persist()
    {
        // Covers AppendStatusPushAuditAsync's `if (!appended)` warning
        // branch: the decision itself must not fail just because the
        // follow-up audit write did.
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "assessment-in-progress");
        harness
            .AuditAppender.AppendAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(false);

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Rejected,
            harness.User,
            ct
        );

        Assert.True(result.IsSuccess);
    }

    /// <summary>
    /// The push is gated behind the same guards as the hops: a decision refused
    /// on a guard (here a conflicting terminal outcome) must not notify the
    /// Registration & Accreditation service.
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

        await harness
            .PushAdapter.DidNotReceiveWithAnyArgs()
            .PushDecisionStatusChangedAsync(
                default,
                default,
                default!,
                default!,
                default!,
                default!,
                default!,
                default,
                ct
            );
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
                WorkItemActionResult.Failure(WorkItemActionFailureCode.ConcurrencyConflict, "raced")
            );

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Approved,
            harness.User,
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.FailureCode);
        await harness
            .ApprovalService.DidNotReceiveWithAnyArgs()
            .RecordApprovedDeterminationAsync(default, default!, default!, ct);
    }

    // --------------------------- idempotency and conflict ---------------------------

    /// <summary>
    /// A double-click or a client retrying a response it never received must
    /// not fail. RA-527: an approval's replay target is the pending-contact
    /// state step 1 lands in; a refusal is still single-step.
    /// </summary>
    [Theory]
    [InlineData("approved-pending-contact", true)]
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
        Assert.Empty(harness.OrderedCalls);
    }

    /// <summary>
    /// The opposite outcome on an already-decided application is a conflict,
    /// never a replay and never last-write-wins. RA-527: so is any decision on
    /// an application that is already Granted — a determination can only be
    /// made once, and step 2 is what made it Granted.
    /// </summary>
    [Theory]
    [InlineData("approved", false)]
    [InlineData("approved", true)]
    [InlineData("rejected", true)]
    [InlineData("withdrawn", true)]
    public async Task A_decision_on_a_closed_application_is_refused(
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
        Assert.Empty(harness.OrderedCalls);
    }

    /// <summary>
    /// RA-527: a refusal on an application already approved at step 1 is a
    /// conflict. The determination is final; nothing is minted, pushed or
    /// written.
    /// </summary>
    [Fact]
    public async Task Refusing_an_application_pending_contact_after_approval_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "approved-pending-contact");

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Rejected,
            harness.User,
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Empty(harness.AppliedActionIds);
        Assert.Empty(harness.OrderedCalls);
    }

    /// <summary>
    /// RA-527: every check runs before any side effect. An item whose frozen
    /// snapshot predates v17 cannot take the step 1 transition, so it is
    /// refused before a number is minted or the hop is taken — otherwise it
    /// would be parked in awaiting-decision and then refused.
    /// </summary>
    [Fact]
    public async Task Approving_an_item_on_pre_v17_rules_is_refused_before_any_side_effect()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "assessment-in-progress");
        var live = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        harness.WorkItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v16",
            States = live.States.Where(s => s.Id != "approved-pending-contact").ToList(),
            Transitions = live
                .Transitions.Where(t => t.ActionId != "make-determination-approved")
                .ToList(),
        };

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Approved,
            harness.User,
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Empty(harness.OrderedCalls);
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
        Assert.Empty(harness.OrderedCalls);
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
        await harness.Persistence.DidNotReceiveWithAnyArgs().GetByIdAsync(default, ct);
    }

    private static ReAccreditationDecisionOutcome Outcome(bool approve) =>
        approve ? ReAccreditationDecisionOutcome.Approved : ReAccreditationDecisionOutcome.Rejected;

    private sealed class Harness
    {
        public Harness(
            string stateId = "assessment-in-progress",
            bool seedWorkItem = true,
            string typeId = ReAccreditationType.Id,
            bool withUserId = true,
            OperatorBackendPushResult? pushResult = null,
            AccreditationNumberResult? numberResult = null
        )
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

            // The pre-commit operator-journey gate for refusals. Default is a
            // successful push so the happy paths proceed; failure/skip paths
            // override it.
            PushAdapter = Substitute.For<IOperatorBackendPushAdapter>();
            PushAdapter
                .PushDecisionStatusChangedAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<DateTime>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(call =>
                {
                    OrderedCalls.Add("registration-accreditation-push");
                    return pushResult ?? OperatorBackendPushResult.Success();
                });

            AuditAppender = Substitute.For<IWorkItemAuditAppender>();
            AuditAppender
                .AppendAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<Dictionary<string, string?>>(),
                    Arg.Any<ClaimsPrincipal>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(true);

            Engine = Substitute.For<IWorkItemService>();
            Engine
                .ApplyActionAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<ClaimsPrincipal>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(call =>
                {
                    var actionId = call.ArgAt<string>(1);
                    AppliedActionIds.Add(actionId);
                    OrderedCalls.Add(actionId);
                    return WorkItemActionResult.Success(WorkItem);
                });

            ApprovalService = Substitute.For<IReAccreditationApprovalService>();
            // Minted before anything is written for an Approved outcome.
            // Default is a successful mint so the happy paths proceed; the
            // failure path overrides it via the numberResult parameter.
            ApprovalService
                .ResolveAccreditationNumberAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<Guid>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(_ =>
                {
                    OrderedCalls.Add("resolve-number");
                    return numberResult ?? AccreditationNumberResult.Success("A25ER5000270036WO");
                });
            ApprovalService
                .RecordApprovedDeterminationAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<ClaimsPrincipal>(),
                    Arg.Any<AccreditationNumberResult>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(call =>
                {
                    OrderedCalls.Add("record-approved-determination");
                    LastRecordedNumberArgument = call.ArgAt<AccreditationNumberResult>(2);
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
                new FakeTimeProvider()
            );
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
        public AccreditationNumberResult? LastRecordedNumberArgument { get; private set; }
    }
}
