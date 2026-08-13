using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-316 unit cover for <see cref="ReAccreditationDulyMakingService"/>.
///
/// The single-write / real-persistence contract lives in
/// <see cref="ReAccreditationUpdatedWaypointPersistenceTests"/> against a real
/// ephemeral MongoDB, because a substituted persistence has no version protocol
/// and cannot see that class of defect. This suite covers the decision logic:
/// guards, refusals, the SLA anchor, the audit trail and the hook fan-out.
/// </summary>
public class ReAccreditationDulyMakingServiceTests
{
    private static readonly DateOnly s_paymentDate = new(2026, 7, 15);

    private static ClaimsPrincipal User(string? userId = "alice-1") =>
        new(
            new ClaimsIdentity(
                userId is null
                    ? [new Claim("cognito:client_id", "test-client")]
                    :
                    [
                        new Claim("cognito:client_id", "test-client"),
                        new Claim("user:id", userId),
                        new Claim("user:name", "Alice Example"),
                    ],
                "test"
            )
        );

    private sealed record Harness(
        ReAccreditationDulyMakingService Service,
        IWorkItemPersistence Persistence,
        List<(string ActionId, string FromStateId)> HookCalls
    );

    private static Harness BuildHarness(WorkItem? workItem, bool conflictOnSave = false)
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        if (workItem is not null)
        {
            persistence
                .GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
                .Returns(workItem);
        }

        if (conflictOnSave)
        {
            persistence
                .ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromException(
                        new WorkItemConcurrencyException(workItem!.Id, expectedVersion: 0))
                );
        }

        var hookCalls = new List<(string, string)>();
        var hook = Substitute.For<IWorkItemPostActionHook>();
        hook.OnActionAppliedAsync(
                Arg.Any<WorkItem>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call =>
            {
                hookCalls.Add((call.ArgAt<string>(1), call.ArgAt<string>(2)));
                return Task.CompletedTask;
            });

        var service = new ReAccreditationDulyMakingService(
            persistence,
            new WorkItemRegistry([new ReAccreditationType()]),
            [hook],
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 11, 10, 0, 0, TimeSpan.Zero)),
            NullLogger<ReAccreditationDulyMakingService>.Instance
        );

        return new Harness(service, persistence, hookCalls);
    }

    private static WorkItem BuildWorkItem(
        string stateId = "submitted",
        string typeId = ReAccreditationType.Id,
        WorkItemTemplateSnapshot? snapshot = null,
        BsonDocument? payload = null,
        Guid? id = null
    ) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            TypeId = typeId,
            StateId = stateId,
            SubmittedAt = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc),
            LastModifiedAt = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc),
            SubmittedBy = "test-client",
            TemplateSnapshot = snapshot ?? WorkItemTemplateSnapshot.Capture(new ReAccreditationType()),
            TemplateVersion = "v11",
            Payload = payload
                ?? new BsonDocument
                {
                    ["organisationName"] = "Acme Ltd",
                    ["applicationReference"] = "RA-123456789",
                    ["chargeAmountPence"] = 327600,
                },
        };

    // ------------------------- the happy path (AC05, AC06) -------------------------

    [Fact]
    public async Task Duly_making_moves_a_submitted_item_to_duly_made()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var harness = BuildHarness(workItem);

        var result = await harness.Service.CompleteDulyMakingAsync(
            workItem.Id,
            s_paymentDate,
            User(),
            ct
        );

        Assert.True(result.IsSuccess);
        Assert.Equal("duly-made", workItem.StateId);
        await harness.Persistence.Received(1).ReplaceAsync(workItem, ct);
    }

    /// <summary>
    /// AC06, the whole point of the story: the 12-week clock runs from the
    /// entered payment date, NOT from now. The fake clock is at 2026-08-11 and
    /// the payment date is 2026-07-15, so a "start at now" regression is
    /// unmissable here.
    /// </summary>
    [Fact]
    public async Task The_sla_clock_is_anchored_to_the_payment_date_not_to_now()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var harness = BuildHarness(workItem);

        await harness.Service.CompleteDulyMakingAsync(workItem.Id, s_paymentDate, User(), ct);

        Assert.NotNull(workItem.SlaClock);
        Assert.Equal(
            new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc),
            workItem.SlaClock!.StartedAt
        );
        Assert.Equal(TimeSpan.FromDays(84), workItem.SlaClock.TargetDuration);
        // Back-dating must actually shorten the remaining window, not merely be
        // recorded: 27 days of the 84 have already elapsed.
        Assert.Equal(
            TimeSpan.FromDays(84 - 27),
            workItem.SlaClock.Remaining(new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc))
        );
    }

    [Fact]
    public async Task The_payment_date_is_stamped_on_the_payload()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var harness = BuildHarness(workItem);

        await harness.Service.CompleteDulyMakingAsync(workItem.Id, s_paymentDate, User(), ct);

        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload!);
        Assert.Equal(s_paymentDate, payload.PaymentDate);
    }

    /// <summary>
    /// RA-249 regression: the payload model is [BsonIgnoreExtraElements], so a
    /// wholesale replace would silently drop every unmodelled key the operator
    /// backend sent. Stamping the payment date must merge.
    /// </summary>
    [Fact]
    public async Task Stamping_the_payment_date_preserves_unmodelled_payload_keys()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(
            payload: new BsonDocument
            {
                ["organisationName"] = "Acme Ltd",
                ["applicationReference"] = "RA-123456789",
                ["chargeAmountPence"] = 327600,
                ["paymentReference"] = "PAY-9",
                // Not on ReAccreditationPayload at all.
                ["someUnmodelledKey"] = "must-survive",
                ["siteAddressLine1"] = "1 Test Street",
            }
        );
        var harness = BuildHarness(workItem);

        await harness.Service.CompleteDulyMakingAsync(workItem.Id, s_paymentDate, User(), ct);

        Assert.Equal("must-survive", workItem.Payload!["someUnmodelledKey"].AsString);
        Assert.Equal("1 Test Street", workItem.Payload["siteAddressLine1"].AsString);
        Assert.Equal(327600, workItem.Payload["chargeAmountPence"].AsInt32);
        Assert.Equal("PAY-9", workItem.Payload["paymentReference"].AsString);
    }

    // ------------------------------ audit (AC08) ------------------------------

    [Fact]
    public async Task The_audit_trail_records_the_transition_and_the_payment_date()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var harness = BuildHarness(workItem);

        await harness.Service.CompleteDulyMakingAsync(workItem.Id, s_paymentDate, User(), ct);

        var applied = Assert.Single(workItem.AuditLog, e => e.Action == "action-applied");
        Assert.Equal("duly-make", applied.Details["actionId"]);
        Assert.Equal("Duly make", applied.Details["actionDisplayName"]);
        Assert.Equal("submitted", applied.Details["fromStateId"]);
        Assert.Equal("duly-made", applied.Details["toStateId"]);
        Assert.Equal("2026-07-15", applied.Details["paymentDate"]);
        Assert.Equal("alice-1", applied.CreatedBy);
        Assert.Equal("Alice Example", applied.CreatedByName);

        var slaStarted = Assert.Single(workItem.AuditLog, e => e.Action == "sla-clock-started");
        Assert.Equal("payment-date", slaStarted.Details["anchoredTo"]);
        Assert.Equal("84", slaStarted.Details["targetDays"]);
    }

    // --------------------------- the updated waypoint ---------------------------

    /// <summary>
    /// An item queried DURING duly-making, since resubmitted, must still be
    /// duly-makeable — and must travel only edges the template declares, so the
    /// audit trail and the operator-backend push both show a from/to pair every
    /// consumer models.
    /// </summary>
    [Fact]
    public async Task An_item_in_the_updated_waypoint_from_duly_making_can_be_duly_made()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(stateId: "updated");
        AddResumeAuditEntry(workItem, "resume-during-duly-making");
        var harness = BuildHarness(workItem);

        var result = await harness.Service.CompleteDulyMakingAsync(
            workItem.Id,
            s_paymentDate,
            User(),
            ct
        );

        Assert.True(result.IsSuccess);
        Assert.Equal("duly-made", workItem.StateId);

        var applied = workItem
            .AuditLog.Where(e => e.Action == "action-applied")
            .Select(e =>
                (
                    e.Details.GetValueOrDefault("actionId"),
                    e.Details.GetValueOrDefault("fromStateId"),
                    e.Details.GetValueOrDefault("toStateId")
                )
            )
            .ToList();

        Assert.Equal(
            [
                ("resume-during-duly-making", "queried", "updated"),
                ("continue-review-during-duly-making", "updated", "submitted"),
                ("duly-make", "submitted", "duly-made"),
            ],
            applied
        );

        // Everything lands in ONE write, discharge included.
        await harness.Persistence.Received(1).ReplaceAsync(workItem, ct);
        // And the from-state on the wire is the modelled 'submitted'.
        Assert.Equal([("duly-make", "submitted")], harness.HookCalls);
    }

    /// <summary>
    /// The other three origins are mid-review. Duly making from there would skip
    /// whole stages, so it is refused — and refused before any mutation.
    /// </summary>
    [Theory]
    [InlineData("resume-during-duly-made")]
    [InlineData("resume-during-assessment")]
    [InlineData("resume-during-decision")]
    public async Task An_item_in_the_updated_waypoint_from_elsewhere_is_refused(
        string resumeActionId
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(stateId: "updated");
        AddResumeAuditEntry(workItem, resumeActionId);
        var harness = BuildHarness(workItem);

        var result = await harness.Service.CompleteDulyMakingAsync(
            workItem.Id,
            s_paymentDate,
            User(),
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Equal("updated", workItem.StateId);
        Assert.Null(workItem.SlaClock);
        await harness.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
        Assert.Empty(harness.HookCalls);
    }

    /// <summary>
    /// An item in <c>updated</c> with no resume history at all cannot have its
    /// origin derived, so it is refused rather than guessed at.
    /// </summary>
    [Fact]
    public async Task An_updated_item_with_no_resume_history_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(stateId: "updated");
        var harness = BuildHarness(workItem);

        var result = await harness.Service.CompleteDulyMakingAsync(
            workItem.Id,
            s_paymentDate,
            User(),
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
    }

    // -------------------------------- guards --------------------------------

    [Fact]
    public async Task A_missing_work_item_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = BuildHarness(null);

        var result = await harness.Service.CompleteDulyMakingAsync(
            Guid.NewGuid(),
            s_paymentDate,
            User(),
            ct
        );

        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task A_work_item_of_another_type_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(typeId: "some-other-type");
        var harness = BuildHarness(workItem);

        var result = await harness.Service.CompleteDulyMakingAsync(
            workItem.Id,
            s_paymentDate,
            User(),
            ct
        );

        Assert.False(result.IsSuccess);
        // The endpoint maps this to 400 + errorCode 'wrong-work-item-type'.
        Assert.Equal(WorkItemActionFailureCode.UnknownAction, result.FailureCode);
    }

    [Theory]
    [InlineData("duly-made")]
    [InlineData("assessment-in-progress")]
    [InlineData("awaiting-decision")]
    [InlineData("queried")]
    public async Task A_work_item_in_the_wrong_state_is_refused(string stateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(stateId: stateId);
        var harness = BuildHarness(workItem);

        var result = await harness.Service.CompleteDulyMakingAsync(
            workItem.Id,
            s_paymentDate,
            User(),
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Equal(stateId, workItem.StateId);
        await harness.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    public async Task A_terminal_work_item_is_refused(string stateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(stateId: stateId);
        var harness = BuildHarness(workItem);

        var result = await harness.Service.CompleteDulyMakingAsync(
            workItem.Id,
            s_paymentDate,
            User(),
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.TerminalState, result.FailureCode);
    }

    /// <summary>
    /// Mutations require a BFF-forwarded end-user identity: the audit entry has
    /// to be attributable to a real human, so without it we refuse rather than
    /// persist a placeholder.
    /// </summary>
    [Fact]
    public async Task A_caller_without_a_user_id_claim_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var harness = BuildHarness(workItem);

        var result = await harness.Service.CompleteDulyMakingAsync(
            workItem.Id,
            s_paymentDate,
            User(userId: null),
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.MissingActorIdentity, result.FailureCode);
        // Refused before the document is even read.
        await harness.Persistence.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
    }

    /// <summary>
    /// Template versioning is a hard framework rule: an in-flight item is judged
    /// by the rules it was submitted under. An item whose frozen snapshot lacks
    /// duly-make is refused rather than carried across an undeclared edge — the
    /// v10 → v11 snapshot migration is what clears this, and it retries every
    /// boot, so the refusal is transient rather than a dead end.
    /// </summary>
    [Fact]
    public async Task An_item_whose_snapshot_lacks_duly_make_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var live = WorkItemTemplateSnapshot.Capture(new ReAccreditationType());
        var v10Snapshot = new WorkItemTemplateSnapshot
        {
            TemplateVersion = "v10",
            States = live.States,
            Transitions = live.Transitions.Where(t => t.ActionId != "duly-make").ToList(),
        };
        var workItem = BuildWorkItem(snapshot: v10Snapshot);
        var harness = BuildHarness(workItem);

        var result = await harness.Service.CompleteDulyMakingAsync(
            workItem.Id,
            s_paymentDate,
            User(),
            ct
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Equal("submitted", workItem.StateId);
        await harness.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }

    /// <summary>
    /// Every save loses the optimistic-concurrency race, so the service
    /// exhausts its retry budget and reports a conflict the frontend renders as
    /// "this application has changed, reload".
    ///
    /// The substitute hands back a FRESH document on each read, which is what
    /// real persistence does. Returning the same in-memory instance would have
    /// the retry re-read an object the previous attempt already mutated to
    /// duly-made, and the second attempt would then fail the state guard
    /// instead — a test artefact that would mask the behaviour under test.
    /// </summary>
    [Fact]
    public async Task Repeated_concurrency_conflicts_surface_as_a_conflict_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();

        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence
            .GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(_ => BuildWorkItem(id: id));
        persistence
            .ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(
                new WorkItemConcurrencyException(id, expectedVersion: 0)));

        var hookCalls = new List<(string, string)>();
        var hook = Substitute.For<IWorkItemPostActionHook>();
        hook.OnActionAppliedAsync(
                Arg.Any<WorkItem>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call =>
            {
                hookCalls.Add((call.ArgAt<string>(1), call.ArgAt<string>(2)));
                return Task.CompletedTask;
            });

        var service = new ReAccreditationDulyMakingService(
            persistence,
            new WorkItemRegistry([new ReAccreditationType()]),
            [hook],
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 11, 10, 0, 0, TimeSpan.Zero)),
            NullLogger<ReAccreditationDulyMakingService>.Instance
        );

        var result = await service.CompleteDulyMakingAsync(id, s_paymentDate, User(), ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.FailureCode);
        // Three attempts, per the service's MaxAttempts — it retries rather
        // than failing the first race, and it stops rather than spinning.
        await persistence.Received(3).ReplaceAsync(Arg.Any<WorkItem>(), ct);
        // No notification and no operator push for an operation that never
        // committed.
        Assert.Empty(hookCalls);
    }

    /// <summary>
    /// A Notify outage or an operator-backend failure must never unwind a duly
    /// making that is already committed to the database.
    /// </summary>
    [Fact]
    public async Task A_throwing_hook_does_not_fail_the_operation()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var hook = Substitute.For<IWorkItemPostActionHook>();
        hook.OnActionAppliedAsync(
                Arg.Any<WorkItem>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(_ => Task.FromException(new InvalidOperationException("notify is down")));

        var service = new ReAccreditationDulyMakingService(
            persistence,
            new WorkItemRegistry([new ReAccreditationType()]),
            [hook],
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 11, 10, 0, 0, TimeSpan.Zero)),
            NullLogger<ReAccreditationDulyMakingService>.Instance
        );

        var result = await service.CompleteDulyMakingAsync(
            workItem.Id,
            s_paymentDate,
            User(),
            ct
        );

        Assert.True(result.IsSuccess);
        Assert.Equal("duly-made", workItem.StateId);
    }

    private static void AddResumeAuditEntry(WorkItem workItem, string resumeActionId) =>
        workItem.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = "action-applied",
                ActionDisplayName = "Action applied",
                CreatedAt = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc),
                Details = new Dictionary<string, string?>
                {
                    ["actionId"] = resumeActionId,
                    ["actionDisplayName"] = "Resume",
                    ["fromStateId"] = "queried",
                    ["toStateId"] = "updated",
                },
            }
        );
}
