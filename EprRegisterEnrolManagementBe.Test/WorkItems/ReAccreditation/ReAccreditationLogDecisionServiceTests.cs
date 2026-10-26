using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-410: one caller-visible call carries a re-accreditation application from
/// <c>assessment-in-progress</c>, through the <c>awaiting-decision</c> hop, to
/// its determination.
///
/// RA-527: a determination is step 1 of two, for either outcome. An Approved
/// determination mints the number and lands in <c>approved-pending-contact</c>
/// through the approval service; a Refused one mints nothing and lands in
/// <c>rejected-pending-contact</c> through the refusal service. Neither pushes
/// to the operator journey — the push-before-write rules live with step 2, in
/// <see cref="ReAccreditationConfirmContactServiceTests"/>.
/// </summary>
public class ReAccreditationLogDecisionServiceTests
{
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
            .ApproveAsync(default, default!, default, ct);
    }

    /// <summary>
    /// RA-527: refusing records the determination (step 1) through the
    /// refusal service, which moves the item to rejected-pending-contact. It
    /// never applies the outcome: RefuseAsync (SLA stop) is step 2's job, and
    /// nothing goes near the approval service.
    /// </summary>
    [Fact]
    public async Task Refusing_from_assessment_applies_submit_for_decision_then_records_the_step_one_refusal()
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
        Assert.Equal(["submit-for-decision"], harness.AppliedActionIds);
        await harness
            .RefusalService.Received(1)
            .RecordRefusedDeterminationAsync(harness.WorkItem.Id, harness.User, ct);
        await harness
            .RefusalService.DidNotReceiveWithAnyArgs()
            .RefuseAsync(default, default!, default, ct);
        Assert.Empty(harness.ApprovalService.ReceivedCalls());
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
    /// RA-527: a refusal takes the hop, then records the determination. There
    /// is no operator-journey push at step 1 (and none at all in this
    /// service, which has no push adapter).
    /// </summary>
    [Fact]
    public async Task Refusing_takes_the_hop_then_records_the_determination()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "assessment-in-progress");

        await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Rejected,
            harness.User,
            ct
        );

        Assert.Equal(["submit-for-decision", "record-refused-determination"], harness.OrderedCalls);
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
        Assert.Equal(
            [approve ? "record-approved-determination" : "record-refused-determination"],
            harness.OrderedCalls.Where(c => c.StartsWith("record-", StringComparison.Ordinal))
        );
    }

    /// <summary>
    /// A failed hop must write nothing and record no decision, so the caller
    /// can simply retry.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failed_submit_for_decision_records_no_outcome(bool approve)
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
            Outcome(approve),
            harness.User,
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.FailureCode);
        await harness
            .ApprovalService.DidNotReceiveWithAnyArgs()
            .RecordApprovedDeterminationAsync(default, default!, default!, ct);
        await harness
            .RefusalService.DidNotReceiveWithAnyArgs()
            .RecordRefusedDeterminationAsync(default, default!, ct);
    }

    // --------------------------- idempotency and conflict ---------------------------

    /// <summary>
    /// A double-click or a client retrying a response it never received must
    /// not fail. RA-527: each outcome replays on the pending-contact state its
    /// step 1 lands in and (task 007, as on main) on its own final state —
    /// nothing moves back to step 1, nothing is minted or written.
    /// </summary>
    [Theory]
    [InlineData("approved-pending-contact", true)]
    [InlineData("rejected-pending-contact", false)]
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
        Assert.Empty(harness.OrderedCalls);
    }

    /// <summary>
    /// The opposite outcome on an already-decided application is a conflict,
    /// never a replay and never last-write-wins, as on main; so is any
    /// decision on a withdrawn application. (The same outcome on its own final
    /// state is a replay: see above.)
    /// </summary>
    [Theory]
    [InlineData("approved", false)]
    [InlineData("rejected", true)]
    [InlineData("withdrawn", true)]
    [InlineData("withdrawn", false)]
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
    /// RA-527: the opposite outcome on an application already determined at
    /// step 1 is a conflict. The determination is final; nothing is minted,
    /// pushed or written.
    /// </summary>
    [Theory]
    [InlineData("approved-pending-contact", false)]
    [InlineData("rejected-pending-contact", true)]
    public async Task The_opposite_outcome_on_an_application_pending_contact_is_refused(
        string stateId,
        bool approve
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: stateId);

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            Outcome(approve),
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
    /// snapshot predates v17 cannot take its outcome's step 1 transition, so
    /// it is refused before a number is minted or the hop is taken —
    /// otherwise it would be parked in awaiting-decision and then refused.
    /// </summary>
    [Theory]
    [InlineData(true, "make-determination-approved")]
    [InlineData(false, "make-determination-rejected")]
    public async Task A_determination_on_pre_v17_rules_is_refused_before_any_side_effect(
        bool approve,
        string stepOneActionId
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "assessment-in-progress");
        var live = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        harness.WorkItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v16",
            States = live.States,
            Transitions = live.Transitions.Where(t => t.ActionId != stepOneActionId).ToList(),
        };

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            Outcome(approve),
            harness.User,
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Empty(harness.OrderedCalls);
    }

    /// <summary>
    /// RA-527 task 007 (restored from main's single-step approve): an item
    /// not yet approved that already carries an accreditation id is refused
    /// with "Manual investigation required" before a number is minted or the
    /// hop is taken. Only the retired generator's legacy shape is treated as
    /// unset.
    /// </summary>
    [Theory]
    [InlineData("A25ER5000270036WO")]
    [InlineData("ACC-2025-A-DEADBEEF")]
    public async Task Approving_an_item_that_already_carries_an_accreditation_id_is_refused_before_the_mint(
        string existingId
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "assessment-in-progress");
        harness.WorkItem.Payload["accreditationId"] = existingId;

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Approved,
            harness.User,
            ct
        );

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Contains("Manual investigation required", result.Message);
        Assert.Empty(harness.AppliedActionIds);
        Assert.Empty(harness.OrderedCalls);
    }

    [Fact]
    public async Task Approving_an_item_with_a_legacy_format_accreditation_id_mints_a_real_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "assessment-in-progress");
        harness.WorkItem.Payload["accreditationId"] = "A25ER00000000000";

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Approved,
            harness.User,
            ct
        );

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(
            ["resolve-number", "submit-for-decision", "record-approved-determination"],
            harness.OrderedCalls
        );
    }

    /// <summary>
    /// A refusal mints nothing, so an existing id does not block it.
    /// </summary>
    [Fact]
    public async Task Refusing_an_item_that_carries_an_accreditation_id_is_not_blocked()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "assessment-in-progress");
        harness.WorkItem.Payload["accreditationId"] = "A25ER5000270036WO";

        var result = await harness.Service.LogDecisionAsync(
            harness.WorkItem.Id,
            ReAccreditationDecisionOutcome.Rejected,
            harness.User,
            ct
        );

        Assert.True(result.IsSuccess, result.Message);
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

            RefusalService = Substitute.For<IReAccreditationRefusalService>();
            RefusalService
                .RecordRefusedDeterminationAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<ClaimsPrincipal>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(_ =>
                {
                    OrderedCalls.Add("record-refused-determination");
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
                RefusalService,
                NullLogger<ReAccreditationLogDecisionService>.Instance
            );
        }

        public WorkItem WorkItem { get; }
        public IWorkItemPersistence Persistence { get; }
        public IWorkItemService Engine { get; }
        public IReAccreditationApprovalService ApprovalService { get; }
        public IReAccreditationRefusalService RefusalService { get; }
        public ClaimsPrincipal User { get; }
        public ReAccreditationLogDecisionService Service { get; }
        public List<string> AppliedActionIds { get; } = [];
        public List<string> OrderedCalls { get; } = [];
        public AccreditationNumberResult? LastRecordedNumberArgument { get; private set; }
    }
}
