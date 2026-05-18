using System.Security.Claims;
using EprRegisterEnrolManagementBe.Utils.Background;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-132: unit-level tests for <see cref="ReAccreditationApprovalService"/>.
/// Persistence, hooks, queue and id generator are all substituted so each
/// branch of the validate → mutate → audit → enqueue → fan-out pipeline
/// can be asserted in isolation.
/// </summary>
public class ReAccreditationApprovalServiceTests
{
    private const string DecisionMakerId = "alice-1";
    private const string OtherTenantClientId = "other-tenant";
    private const string OwnerClientId = "test-client";

    private static readonly DateTimeOffset s_fixedNow =
        new(2025, 02, 03, 12, 30, 0, TimeSpan.Zero);

    private static ClaimsPrincipal DecisionMaker(string? clientId = OwnerClientId) =>
        new(new ClaimsIdentity(
        [
            new Claim("user:id", DecisionMakerId),
            new Claim("user:name", "Alice Example"),
            new Claim("cognito:client_id", clientId ?? OwnerClientId),
            new Claim(ClaimTypes.Role, ReAccreditationType.DecisionMakerRole)
        ], "test"));

    private static ClaimsPrincipal AnonymousUser() =>
        new(new ClaimsIdentity(
        [
            new Claim("cognito:client_id", OwnerClientId),
            new Claim(ClaimTypes.Role, ReAccreditationType.DecisionMakerRole)
        ], "test"));

    private static ClaimsPrincipal AssessorOnly() =>
        new(new ClaimsIdentity(
        [
            new Claim("user:id", DecisionMakerId),
            new Claim("user:name", "Alice Example"),
            new Claim("cognito:client_id", OwnerClientId)
        ], "test"));

    private static WorkItem BuildWorkItem(
        string stateId = "assessment-in-progress",
        string? submittedBy = OwnerClientId,
        BsonDocument? payload = null,
        string typeId = ReAccreditationType.Id)
    {
        var type = new ReAccreditationType();
        return new WorkItem
        {
            TypeId = typeId,
            StateId = stateId,
            SubmittedBy = submittedBy,
            Payload = payload ?? new BsonDocument
            {
                ["organisationName"] = "Acme Ltd",
                ["registrationNumber"] = "EX-001"
            },
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(type),
            TemplateVersion = type.TemplateVersion
        };
    }

    private sealed record Sut(
        ReAccreditationApprovalService Service,
        IWorkItemPersistence Persistence,
        IAccreditationIdGenerator IdGenerator,
        IBackgroundTaskQueue Queue,
        List<IWorkItemPostActionHook> Hooks,
        FakeTimeProvider Time);

    private static Sut Build(string accreditationId = "RA-DEADBEEF")
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        var idGenerator = Substitute.For<IAccreditationIdGenerator>();
        idGenerator.Generate().Returns(accreditationId);
        var queue = Substitute.For<IBackgroundTaskQueue>();
        var hooks = new List<IWorkItemPostActionHook> { Substitute.For<IWorkItemPostActionHook>() };
        var time = new FakeTimeProvider(s_fixedNow);

        var sut = new ReAccreditationApprovalService(
            persistence,
            idGenerator,
            queue,
            hooks,
            NullLogger<ReAccreditationApprovalService>.Instance,
            time);

        return new Sut(sut, persistence, idGenerator, queue, hooks, time);
    }

    // ─────────────────────────── happy path ───────────────────────────

    [Fact]
    public async Task ApproveAsync_stamps_payload_transitions_state_appends_three_audit_entries_and_fans_out()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build("RA-12345678");
        var workItem = BuildWorkItem();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.True(result.IsSuccess);
        Assert.Equal("approved", workItem.StateId);
        Assert.Equal(s_fixedNow.UtcDateTime, workItem.LastModifiedAt);

        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        Assert.Equal("RA-12345678", payload.AccreditationId);
        Assert.Equal(DateOnly.FromDateTime(s_fixedNow.UtcDateTime), payload.AccreditationStartDate);
        Assert.NotNull(payload.SlaClock);
        Assert.Equal(s_fixedNow, payload.SlaClock!.StoppedAt);
        // RA-132 must not nuke existing payload fields.
        Assert.Equal("Acme Ltd", payload.OrganisationName);

        Assert.Equal(3, workItem.AuditLog.Count);
        Assert.Equal("action-applied", workItem.AuditLog[0].Action);
        Assert.Equal("approve", workItem.AuditLog[0].Details["actionId"]);
        Assert.Equal("approved", workItem.AuditLog[0].Details["toStateId"]);
        Assert.Equal("sla-clock-stopped", workItem.AuditLog[1].Action);
        Assert.Equal("accreditation-issued", workItem.AuditLog[2].Action);
        Assert.Equal("RA-12345678", workItem.AuditLog[2].Details["accreditationId"]);

        await sut.Persistence.Received(1).ReplaceAsync(workItem, Arg.Any<CancellationToken>());
        await sut.Queue.Received(1).QueueAsync(
            Arg.Any<Func<IServiceProvider, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
        await sut.Hooks[0].Received(1).OnActionAppliedAsync(
            workItem, "approve", "assessment-in-progress", Arg.Any<ClaimsPrincipal>(), ct);
    }

    [Fact]
    public async Task Queued_publishing_audit_runs_against_scoped_appender_with_accreditation_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build("RA-CAFEBABE");
        var workItem = BuildWorkItem();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        Func<IServiceProvider, CancellationToken, Task>? captured = null;
        await sut.Queue.QueueAsync(
            Arg.Do<Func<IServiceProvider, CancellationToken, Task>>(j => captured = j),
            Arg.Any<CancellationToken>());

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.True(result.IsSuccess);
        Assert.NotNull(captured);

        var appender = Substitute.For<IWorkItemAuditAppender>();
        var services = new ServiceCollection();
        services.AddSingleton(appender);
        await using var sp = services.BuildServiceProvider();

        await captured!(sp, ct);

        await appender.Received(1).AppendAsync(
            workItem.Id,
            "publishing-enqueued",
            Arg.Any<string>(),
            Arg.Is<Dictionary<string, string?>>(d => d["accreditationId"] == "RA-CAFEBABE"),
            Arg.Any<ClaimsPrincipal>(),
            ct);
    }

    // ─────────────────────────── validation paths ──────────────────────

    [Fact]
    public async Task Returns_MissingActorIdentity_when_user_has_no_user_id_claim()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();

        var result = await sut.Service.ApproveAsync(Guid.NewGuid(), AnonymousUser(), ct);

        Assert.Equal(WorkItemActionFailureCode.MissingActorIdentity, result.FailureCode);
        await sut.Persistence.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
    }

    [Fact]
    public async Task Returns_WorkItemNotFound_when_persistence_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        sut.Persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);

        var result = await sut.Service.ApproveAsync(Guid.NewGuid(), DecisionMaker(), ct);

        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task Returns_WorkItemNotFound_when_caller_cannot_read_work_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(submittedBy: OtherTenantClientId);
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task Returns_UnknownAction_when_work_item_is_wrong_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(typeId: "some-other-type");
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.Equal(WorkItemActionFailureCode.UnknownAction, result.FailureCode);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    public async Task Returns_TerminalState_for_already_terminal_work_item(string stateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(stateId: stateId);
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.Equal(WorkItemActionFailureCode.TerminalState, result.FailureCode);
    }

    [Theory]
    [InlineData("submitted")]
    [InlineData("duly-made")]
    [InlineData("awaiting-decision")]
    public async Task Returns_InvalidTransition_for_non_assessment_in_progress_states(string stateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(stateId: stateId);
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
    }

    [Fact]
    public async Task Returns_NotAuthorized_when_user_lacks_decision_maker_role()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, AssessorOnly(), ct);

        Assert.Equal(WorkItemActionFailureCode.NotAuthorized, result.FailureCode);
    }

    [Fact]
    public async Task Tolerates_unparseable_existing_payload_and_rebuilds_from_scratch()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build("RA-AAAAAAAA");
        // Force the deserialiser to throw by stuffing a malformed value
        // into a typed field.
        var workItem = BuildWorkItem(payload: new BsonDocument
        {
            ["accreditationStartDate"] = "not-a-date"
        });
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.True(result.IsSuccess);
        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        Assert.Equal("RA-AAAAAAAA", payload.AccreditationId);
        Assert.Equal(DateOnly.FromDateTime(s_fixedNow.UtcDateTime), payload.AccreditationStartDate);
    }

    [Fact]
    public async Task Logs_and_continues_when_a_post_action_hook_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);
        sut.Hooks[0].OnActionAppliedAsync(
                Arg.Any<WorkItem>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("hook boom"));

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.True(result.IsSuccess);
    }

    // ─────────────────────────── concurrency retry ─────────────────────

    [Fact]
    public async Task Retries_on_concurrency_conflict_and_succeeds_within_max_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        // Hand back a fresh work-item per call so each retry sees a
        // clean assessment-in-progress doc (the production load would).
        sut.Persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => BuildWorkItem());

        var calls = 0;
        sut.Persistence
            .When(p => p.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                calls++;
                if (calls == 1)
                {
                    var item = call.Arg<WorkItem>();
                    throw new WorkItemConcurrencyException(item.Id, expectedVersion: 0);
                }
            });

        var result = await sut.Service.ApproveAsync(Guid.NewGuid(), DecisionMaker(), ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Returns_ConcurrencyConflict_after_three_failed_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        sut.Persistence.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => BuildWorkItem());
        sut.Persistence
            .When(p => p.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var item = call.Arg<WorkItem>();
                throw new WorkItemConcurrencyException(item.Id, expectedVersion: 0);
            });

        var result = await sut.Service.ApproveAsync(Guid.NewGuid(), DecisionMaker(), ct);

        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.FailureCode);
        await sut.Persistence.Received(3).ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApproveAsync_throws_when_user_is_null()
    {
        var sut = Build();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => sut.Service.ApproveAsync(Guid.NewGuid(), user: null!));
    }
}
