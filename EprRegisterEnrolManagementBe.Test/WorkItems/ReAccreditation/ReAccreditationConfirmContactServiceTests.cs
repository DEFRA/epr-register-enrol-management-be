using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-527 step 2 ("confirm applicant contact"), for both outcomes. The push-before-write rules
/// carried over from <see cref="ReAccreditationLogDecisionServiceTests"/>:
/// the operator journey is told first, with one timestamp that the write then
/// uses to stop the SLA clock; a failed push applies nothing; a skipped push
/// is a pass; the push outcome is audited once the write lands; and a guard
/// failure never reaches the operator journey.
/// </summary>
public class ReAccreditationConfirmContactServiceTests
{
    private static readonly DateTimeOffset s_now = new(2025, 03, 04, 09, 15, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_defaults_time_provider_when_omitted()
    {
        var service = new ReAccreditationConfirmContactService(
            Substitute.For<IWorkItemPersistence>(),
            new WorkItemRegistry([new ReAccreditationType()]),
            Substitute.For<IReAccreditationApprovalService>(),
            Substitute.For<IReAccreditationRefusalService>(),
            Substitute.For<IOperatorBackendPushAdapter>(),
            Substitute.For<IWorkItemAuditAppender>(),
            NullLogger<ReAccreditationConfirmContactService>.Instance
        );

        Assert.NotNull(service);
    }

    /// <summary>
    /// The push is the gate: it lands before the write, carries to-state
    /// approved / from-state approved-pending-contact, and its OccurredAt is
    /// the same instant the write stops the SLA clock at.
    /// </summary>
    [Fact]
    public async Task Pushes_granted_to_the_operator_journey_then_applies_the_approval_at_the_same_instant()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness();

        var result = await harness.Service.ConfirmContactAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(["registration-accreditation-push", "approve"], harness.OrderedCalls);
        await harness
            .PushAdapter.Received(1)
            .PushDecisionStatusChangedAsync(
                harness.WorkItem.Id,
                Arg.Any<Guid>(),
                "approved-pending-contact",
                "approved",
                "Granted",
                "confirm-contact-approved",
                "Confirm applicant contact",
                s_now.UtcDateTime,
                ct
            );
        await harness
            .ApprovalService.Received(1)
            .ApproveAsync(harness.WorkItem.Id, harness.User, s_now, ct);
    }

    [Fact]
    public async Task A_successful_confirmation_records_a_status_push_sent_audit_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness();

        await harness.Service.ConfirmContactAsync(harness.WorkItem.Id, harness.User, ct);

        await harness
            .AuditAppender.Received(1)
            .AppendAsync(
                harness.WorkItem.Id,
                "status-push-sent",
                "Status sent to the Registration & Accreditation service",
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["actionId"] == "confirm-contact-approved"
                    && d["fromStateId"] == "approved-pending-contact"
                    && d["toStateId"] == "approved"
                    && d["toStateDisplayName"] == "Granted"
                ),
                harness.User,
                ct
            );
    }

    /// <summary>
    /// The operator journey unreachable after the retry budget: nothing is
    /// applied (the approval service is never called) and the caller gets an
    /// UpstreamNotificationFailed the endpoint maps to a 500. A
    /// status-push-failed entry is still recorded for support.
    /// </summary>
    [Fact]
    public async Task A_push_failure_applies_nothing_and_maps_to_500()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(
            pushResult: OperatorBackendPushResult.Failure("operator journey unreachable")
        );

        var result = await harness.Service.ConfirmContactAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.Equal(WorkItemActionFailureCode.UpstreamNotificationFailed, result.FailureCode);
        await harness
            .ApprovalService.DidNotReceiveWithAnyArgs()
            .ApproveAsync(default, default!, default, ct);
        await harness
            .AuditAppender.Received(1)
            .AppendAsync(
                harness.WorkItem.Id,
                "status-push-failed",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["errorMessage"] == "operator journey unreachable"
                ),
                harness.User,
                ct
            );
    }

    /// <summary>
    /// A disabled push (Skipped) is a pass — confirmation must still work
    /// where the push is switched off — recorded under status-push-skipped.
    /// </summary>
    [Fact]
    public async Task A_skipped_push_still_applies_the_approval()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(
            pushResult: OperatorBackendPushResult.Skipped("OperatorBackendApi:Enabled is false.")
        );

        var result = await harness.Service.ConfirmContactAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.True(result.IsSuccess, result.Message);
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

    [Fact]
    public async Task A_confirmation_still_succeeds_when_the_status_push_audit_entry_fails_to_persist()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness();
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

        var result = await harness.Service.ConfirmContactAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.True(result.IsSuccess, result.Message);
    }

    /// <summary>
    /// If the write fails after a delivered push (a concurrent change), the
    /// failure is returned for the caller to retry, and no status-push-sent
    /// entry claims an outcome that did not land.
    /// </summary>
    [Fact]
    public async Task A_write_failure_after_the_push_is_returned_without_a_sent_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(
            approveResult: WorkItemActionResult.Failure(
                WorkItemActionFailureCode.ConcurrencyConflict,
                "modified concurrently"
            )
        );

        var result = await harness.Service.ConfirmContactAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.FailureCode);
        await harness
            .AuditAppender.DidNotReceiveWithAnyArgs()
            .AppendAsync(default, default!, default!, default!, default!, ct);
    }

    // --------------------------- a refusal ---------------------------

    /// <summary>
    /// RA-527: the same run order for a refusal. The push lands before the
    /// write, carries to-state rejected / from-state rejected-pending-contact,
    /// and its OccurredAt is the instant the refusal service stops the SLA
    /// clock at. The approval service is never involved.
    /// </summary>
    [Fact]
    public async Task Pushes_refused_to_the_operator_journey_then_applies_the_refusal_at_the_same_instant()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "rejected-pending-contact");
        harness.WorkItem.Payload.Remove("accreditationId");

        var result = await harness.Service.ConfirmContactAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(["registration-accreditation-push", "refuse"], harness.OrderedCalls);
        await harness
            .PushAdapter.Received(1)
            .PushDecisionStatusChangedAsync(
                harness.WorkItem.Id,
                Arg.Any<Guid>(),
                "rejected-pending-contact",
                "rejected",
                "Refused",
                "confirm-contact-rejected",
                "Confirm applicant contact",
                s_now.UtcDateTime,
                ct
            );
        await harness
            .RefusalService.Received(1)
            .RefuseAsync(harness.WorkItem.Id, harness.User, s_now, ct);
        Assert.Empty(harness.ApprovalService.ReceivedCalls());
        await harness
            .AuditAppender.Received(1)
            .AppendAsync(
                harness.WorkItem.Id,
                "status-push-sent",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["actionId"] == "confirm-contact-rejected" && d["toStateId"] == "rejected"
                ),
                harness.User,
                ct
            );
    }

    [Fact]
    public async Task A_push_failure_applies_no_refusal_and_maps_to_500()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(
            stateId: "rejected-pending-contact",
            pushResult: OperatorBackendPushResult.Failure("operator journey unreachable")
        );

        var result = await harness.Service.ConfirmContactAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.Equal(WorkItemActionFailureCode.UpstreamNotificationFailed, result.FailureCode);
        await harness
            .RefusalService.DidNotReceiveWithAnyArgs()
            .RefuseAsync(default, default!, default, ct);
    }

    [Fact]
    public async Task A_refusal_on_rules_without_its_confirm_contact_transition_is_refused_before_the_push()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: "rejected-pending-contact");
        var snapshot = harness.WorkItem.TemplateSnapshot!;
        harness.WorkItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v16",
            States = snapshot.States,
            Transitions = snapshot
                .Transitions.Where(t => t.ActionId != "confirm-contact-rejected")
                .ToList(),
        };

        var result = await harness.Service.ConfirmContactAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Empty(harness.OrderedCalls);
    }

    // --------------------------- guards: nothing reaches the operator journey ---------------------------

    /// <summary>
    /// Already Granted or Refused (a retry, or a second click): an idempotent
    /// replay with no push and no write.
    /// </summary>
    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    public async Task Confirming_a_decided_item_is_an_idempotent_replay_with_no_push(string stateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: stateId);

        var result = await harness.Service.ConfirmContactAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsIdempotentReplay);
        Assert.Empty(harness.OrderedCalls);
    }

    [Theory]
    [InlineData("withdrawn", WorkItemActionFailureCode.TerminalState)]
    [InlineData("assessment-in-progress", WorkItemActionFailureCode.InvalidTransition)]
    [InlineData("awaiting-decision", WorkItemActionFailureCode.InvalidTransition)]
    [InlineData("queried", WorkItemActionFailureCode.InvalidTransition)]
    public async Task Confirming_outside_a_pending_contact_state_is_refused_before_the_push(
        string stateId,
        WorkItemActionFailureCode expected
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(stateId: stateId);

        var result = await harness.Service.ConfirmContactAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.Equal(expected, result.FailureCode);
        Assert.Empty(harness.OrderedCalls);
    }

    /// <summary>
    /// Every rule the write would apply is checked before the push, so the
    /// operator is never told "Granted" about an item the write then refuses:
    /// here, an item pending contact that lacks the step 1 number.
    /// </summary>
    [Fact]
    public async Task An_item_without_the_step_one_number_is_refused_before_the_push()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness();
        harness.WorkItem.Payload.Remove("accreditationId");

        var result = await harness.Service.ConfirmContactAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Empty(harness.OrderedCalls);
    }

    [Fact]
    public async Task An_item_on_rules_without_the_confirm_contact_transition_is_refused_before_the_push()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness();
        var snapshot = harness.WorkItem.TemplateSnapshot!;
        harness.WorkItem.TemplateSnapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v16",
            States = snapshot.States,
            Transitions = snapshot
                .Transitions.Where(t => t.ActionId != "confirm-contact-approved")
                .ToList(),
        };

        var result = await harness.Service.ConfirmContactAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Empty(harness.OrderedCalls);
    }

    [Fact]
    public async Task Returns_not_found_for_an_unknown_work_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(seedWorkItem: false);

        var result = await harness.Service.ConfirmContactAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
        Assert.Empty(harness.OrderedCalls);
    }

    [Fact]
    public async Task Refuses_a_work_item_of_another_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(typeId: "some-other-type");

        var result = await harness.Service.ConfirmContactAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.Equal(WorkItemActionFailureCode.UnknownAction, result.FailureCode);
        Assert.Empty(harness.OrderedCalls);
    }

    [Fact]
    public async Task Refuses_a_caller_with_no_user_id_claim_before_touching_the_work_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = new Harness(withUserId: false);

        var result = await harness.Service.ConfirmContactAsync(harness.WorkItem.Id, harness.User, ct);

        Assert.Equal(WorkItemActionFailureCode.MissingActorIdentity, result.FailureCode);
        Assert.Empty(harness.OrderedCalls);
        await harness.Persistence.DidNotReceiveWithAnyArgs().GetByIdAsync(default, ct);
    }

    private sealed class Harness
    {
        public Harness(
            string stateId = "approved-pending-contact",
            bool seedWorkItem = true,
            string typeId = ReAccreditationType.Id,
            bool withUserId = true,
            OperatorBackendPushResult? pushResult = null,
            WorkItemActionResult? approveResult = null
        )
        {
            var type = new ReAccreditationType();
            WorkItem = new WorkItem
            {
                TypeId = typeId,
                StateId = stateId,
                SubmittedBy = "test-client",
                Payload = new BsonDocument { ["accreditationId"] = "A25ER5000270036WO" },
                TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
                TemplateVersion = type.TemplateVersion,
            };

            Persistence = Substitute.For<IWorkItemPersistence>();
            Persistence
                .GetByIdAsync(WorkItem.Id, Arg.Any<CancellationToken>())
                .Returns(seedWorkItem ? WorkItem : null);

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
                .Returns(_ =>
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

            ApprovalService = Substitute.For<IReAccreditationApprovalService>();
            ApprovalService
                .ApproveAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<ClaimsPrincipal>(),
                    Arg.Any<DateTimeOffset>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(_ =>
                {
                    OrderedCalls.Add("approve");
                    return approveResult ?? WorkItemActionResult.Success(WorkItem);
                });

            RefusalService = Substitute.For<IReAccreditationRefusalService>();
            RefusalService
                .RefuseAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<ClaimsPrincipal>(),
                    Arg.Any<DateTimeOffset>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(_ =>
                {
                    OrderedCalls.Add("refuse");
                    return WorkItemActionResult.Success(WorkItem);
                });

            var claims = new List<Claim> { new("client_id", "test-client") };
            if (withUserId)
            {
                claims.Add(new Claim("user:id", "alice-1"));
                claims.Add(new Claim("user:name", "Alice Example"));
            }
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

            Service = new ReAccreditationConfirmContactService(
                Persistence,
                new WorkItemRegistry([type]),
                ApprovalService,
                RefusalService,
                PushAdapter,
                AuditAppender,
                NullLogger<ReAccreditationConfirmContactService>.Instance,
                new FakeTimeProvider(s_now)
            );
        }

        public WorkItem WorkItem { get; }
        public IWorkItemPersistence Persistence { get; }
        public IReAccreditationApprovalService ApprovalService { get; }
        public IReAccreditationRefusalService RefusalService { get; }
        public IOperatorBackendPushAdapter PushAdapter { get; }
        public IWorkItemAuditAppender AuditAppender { get; }
        public ClaimsPrincipal User { get; }
        public ReAccreditationConfirmContactService Service { get; }
        public List<string> OrderedCalls { get; } = [];
    }
}
