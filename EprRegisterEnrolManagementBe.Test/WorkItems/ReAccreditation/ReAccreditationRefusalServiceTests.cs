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
/// RA-527: unit-level tests for <see cref="ReAccreditationRefusalService"/>,
/// the two writes of a refusal. Persistence and hooks are substituted so the
/// guard → mutate → audit → write → hooks pipeline, its retry on a concurrency
/// conflict and its refusal to overwrite a corrupt payload can be asserted
/// directly. The HTTP-level behaviour (push before write, no email, replays,
/// 409s) is covered by <see cref="ReAccreditationEndpointTests"/>.
/// </summary>
public class ReAccreditationRefusalServiceTests
{
    private static readonly DateTimeOffset s_now = new(2025, 02, 03, 12, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_appliedAt = new(2025, 03, 04, 09, 15, 0, TimeSpan.Zero);

    private static readonly ClaimsPrincipal s_user = new(
        new ClaimsIdentity(
            [new Claim("user:id", "alice-1"), new Claim("user:name", "Alice Example")],
            "test"
        )
    );

    private static WorkItem BuildWorkItem(string stateId, BsonDocument? payload = null)
    {
        var type = new ReAccreditationType();
        return new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            SubmittedBy = "test-client",
            Payload =
                payload
                ?? new BsonDocument
                {
                    ["organisationName"] = "Acme Ltd",
                    ["applicationReference"] = "RA-527-REF",
                    ["nation"] = "England",
                },
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion,
        };
    }

    private sealed record Sut(
        ReAccreditationRefusalService Service,
        IWorkItemPersistence Persistence,
        IWorkItemPostActionHook Hook
    );

    private static Sut Build()
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        var hook = Substitute.For<IWorkItemPostActionHook>();
        var service = new ReAccreditationRefusalService(
            persistence,
            new WorkItemRegistry([new ReAccreditationType()]),
            [hook],
            NullLogger<ReAccreditationRefusalService>.Instance,
            new FakeTimeProvider(s_now)
        );
        return new Sut(service, persistence, hook);
    }

    private static Sut BuildReturning(WorkItem workItem)
    {
        var sut = Build();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);
        return sut;
    }

    // ──────────────── step 1: RecordRefusedDeterminationAsync ────────────────

    [Fact]
    public async Task Step_one_moves_to_rejected_pending_contact_and_records_the_determination()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem("awaiting-decision");
        var sut = BuildReturning(workItem);

        var result = await sut.Service.RecordRefusedDeterminationAsync(workItem.Id, s_user, ct);

        Assert.True(result.IsSuccess, result.Message);
        await sut.Persistence.Received(1).ReplaceAsync(workItem, ct);
        Assert.Equal("rejected-pending-contact", workItem.StateId);
        Assert.Equal(s_now.UtcDateTime, workItem.LastModifiedAt);
        Assert.Equal(
            ["action-applied", "determination-made"],
            workItem.AuditLog.Select(a => a.Action)
        );
        var applied = workItem.AuditLog[0];
        Assert.Equal("make-determination-rejected", applied.Details["actionId"]);
        Assert.Equal("awaiting-decision", applied.Details["fromStateId"]);
        Assert.Equal("rejected-pending-contact", applied.Details["toStateId"]);
        var determination = workItem.AuditLog[1];
        Assert.Equal("Determination made: Refused", determination.ActionDisplayName);
        Assert.Equal("alice-1", determination.CreatedBy);
        Assert.Equal("Alice Example", determination.CreatedByName);
        Assert.Equal("rejected-pending-contact", determination.StateId);

        // Nothing applied: the payload (and so its SLA clock) is untouched.
        Assert.False(workItem.Payload.Contains("slaClock"));
        await sut
            .Hook.Received(1)
            .OnActionAppliedAsync(
                workItem,
                "make-determination-rejected",
                "awaiting-decision",
                s_user,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Step_one_is_an_idempotent_replay_when_already_pending_contact()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem("rejected-pending-contact");
        var sut = BuildReturning(workItem);

        var result = await sut.Service.RecordRefusedDeterminationAsync(workItem.Id, s_user, ct);

        Assert.True(result.IsIdempotentReplay);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Theory]
    [InlineData("approved", WorkItemActionFailureCode.TerminalState)]
    [InlineData("rejected", WorkItemActionFailureCode.TerminalState)]
    [InlineData("withdrawn", WorkItemActionFailureCode.TerminalState)]
    [InlineData("assessment-in-progress", WorkItemActionFailureCode.InvalidTransition)]
    [InlineData("approved-pending-contact", WorkItemActionFailureCode.InvalidTransition)]
    public async Task Step_one_refuses_outside_awaiting_decision(
        string stateId,
        WorkItemActionFailureCode expected
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(stateId);
        var sut = BuildReturning(workItem);

        var result = await sut.Service.RecordRefusedDeterminationAsync(workItem.Id, s_user, ct);

        Assert.Equal(expected, result.FailureCode);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    // ──────────────── step 2: RefuseAsync ────────────────

    [Fact]
    public async Task Step_two_moves_to_rejected_and_stops_the_sla_clock_at_the_callers_instant()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem("rejected-pending-contact");
        var sut = BuildReturning(workItem);

        var result = await sut.Service.RefuseAsync(workItem.Id, s_user, s_appliedAt, ct);

        Assert.True(result.IsSuccess, result.Message);
        await sut.Persistence.Received(1).ReplaceAsync(workItem, ct);
        Assert.Equal("rejected", workItem.StateId);
        Assert.Equal(s_appliedAt.UtcDateTime, workItem.LastModifiedAt);
        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        Assert.Equal(s_appliedAt, payload.SlaClock?.StoppedAt);
        // An unmodelled key survives the payload round-trip.
        Assert.Equal("RA-527-REF", workItem.Payload["applicationReference"].AsString);

        Assert.Equal(
            ["action-applied", "applicant-contacted", "sla-clock-stopped"],
            workItem.AuditLog.Select(a => a.Action)
        );
        Assert.Equal("confirm-contact-rejected", workItem.AuditLog[0].Details["actionId"]);
        var contacted = workItem.AuditLog[1];
        Assert.Equal("Applicant contacted", contacted.ActionDisplayName);
        Assert.Equal("alice-1", contacted.CreatedBy);
        Assert.Equal(s_appliedAt.ToString("O"), workItem.AuditLog[2].Details["stoppedAt"]);

        await sut
            .Hook.Received(1)
            .OnActionAppliedAsync(
                workItem,
                "confirm-contact-rejected",
                "rejected-pending-contact",
                s_user,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Step_two_is_an_idempotent_replay_when_already_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem("rejected");
        var sut = BuildReturning(workItem);

        var result = await sut.Service.RefuseAsync(workItem.Id, s_user, s_appliedAt, ct);

        Assert.True(result.IsIdempotentReplay);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    /// <summary>
    /// The write names its outcome, so it can never refuse an approval that
    /// is pending contact (or a Granted item).
    /// </summary>
    [Theory]
    [InlineData("approved-pending-contact", WorkItemActionFailureCode.InvalidTransition)]
    [InlineData("approved", WorkItemActionFailureCode.TerminalState)]
    [InlineData("awaiting-decision", WorkItemActionFailureCode.InvalidTransition)]
    public async Task Step_two_refuses_outside_rejected_pending_contact(
        string stateId,
        WorkItemActionFailureCode expected
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(stateId);
        var sut = BuildReturning(workItem);

        var result = await sut.Service.RefuseAsync(workItem.Id, s_user, s_appliedAt, ct);

        Assert.Equal(expected, result.FailureCode);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }

    [Fact]
    public async Task Step_two_does_not_overwrite_a_corrupt_payload()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(
            "rejected-pending-contact",
            new BsonDocument { ["accreditationStartDate"] = "not-a-date" }
        );
        var sut = BuildReturning(workItem);

        var result = await sut.Service.RefuseAsync(workItem.Id, s_user, s_appliedAt, ct);

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        Assert.Equal("not-a-date", workItem.Payload["accreditationStartDate"].AsString);
    }

    // ──────────────── shared: identity, hooks, concurrency ────────────────

    [Fact]
    public async Task Refuses_a_caller_with_no_user_id_before_loading_the_work_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([], "test"));

        var result = await sut.Service.RefuseAsync(Guid.NewGuid(), anonymous, s_appliedAt, ct);

        Assert.Equal(WorkItemActionFailureCode.MissingActorIdentity, result.FailureCode);
        await sut.Persistence.DidNotReceiveWithAnyArgs().GetByIdAsync(default, ct);
    }

    [Fact]
    public async Task A_throwing_hook_does_not_fail_the_committed_write()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem("rejected-pending-contact");
        var sut = BuildReturning(workItem);
        sut.Hook.OnActionAppliedAsync(
                Arg.Any<WorkItem>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(_ => throw new InvalidOperationException("hook boom"));

        var result = await sut.Service.RefuseAsync(workItem.Id, s_user, s_appliedAt, ct);

        Assert.True(result.IsSuccess, result.Message);
    }

    [Fact]
    public async Task Retries_on_a_concurrency_conflict_and_succeeds_within_max_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        sut.Persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => BuildWorkItem("rejected-pending-contact"));
        var calls = 0;
        sut.Persistence.When(p => p.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                if (++calls == 1)
                {
                    throw new WorkItemConcurrencyException(call.Arg<WorkItem>().Id, expectedVersion: 0);
                }
            });

        var result = await sut.Service.RefuseAsync(Guid.NewGuid(), s_user, s_appliedAt, ct);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, calls);
        Assert.Equal(3, result.WorkItem!.AuditLog.Count);
    }

    /// <summary>
    /// The guard runs again on every attempt: a concurrent confirmation that
    /// landed first turns the retry into a replay with no second write.
    /// </summary>
    [Fact]
    public async Task A_concurrent_confirmation_that_landed_first_turns_the_retry_into_a_replay()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        sut.Persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => BuildWorkItem("rejected-pending-contact"), _ => BuildWorkItem("rejected"));
        sut.Persistence.When(p => p.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()))
            .Do(call => throw new WorkItemConcurrencyException(call.Arg<WorkItem>().Id, expectedVersion: 0));

        var result = await sut.Service.RefuseAsync(Guid.NewGuid(), s_user, s_appliedAt, ct);

        Assert.True(result.IsIdempotentReplay);
        await sut.Persistence.ReceivedWithAnyArgs(1).ReplaceAsync(default!, ct);
        await sut.Hook.DidNotReceiveWithAnyArgs().OnActionAppliedAsync(default!, default!, default!, default!, ct);
    }

    [Fact]
    public async Task Returns_ConcurrencyConflict_after_three_failed_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        sut.Persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => BuildWorkItem("awaiting-decision"));
        sut.Persistence.When(p => p.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()))
            .Do(call => throw new WorkItemConcurrencyException(call.Arg<WorkItem>().Id, expectedVersion: 0));

        var result = await sut.Service.RecordRefusedDeterminationAsync(Guid.NewGuid(), s_user, ct);

        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.FailureCode);
        await sut.Persistence.Received(3).ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
        await sut.Hook.DidNotReceiveWithAnyArgs().OnActionAppliedAsync(default!, default!, default!, default!, ct);
    }
}
