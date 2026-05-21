using System.Security.Claims;
using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Utils.Background;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    private static Sut Build(
        string accreditationId = "ACC-2025-A-DEADBEEF",
        int currentYear = 2025,
        DateTimeOffset? now = null)
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        var idGenerator = Substitute.For<IAccreditationIdGenerator>();
        idGenerator.GenerateAsync(
                Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(accreditationId));
        var queue = Substitute.For<IBackgroundTaskQueue>();
        var hooks = new List<IWorkItemPostActionHook> { Substitute.For<IWorkItemPostActionHook>() };
        var time = new FakeTimeProvider(now ?? s_fixedNow);

        var sut = new ReAccreditationApprovalService(
            persistence,
            idGenerator,
            queue,
            hooks,
            NullLogger<ReAccreditationApprovalService>.Instance,
            Options.Create(new AccreditationConfig { CurrentYear = currentYear }),
            time);

        return new Sut(sut, persistence, idGenerator, queue, hooks, time);
    }

    // ─────────────────────────── happy path ───────────────────────────

    [Fact]
    public async Task ApproveAsync_stamps_payload_transitions_state_appends_three_audit_entries_and_fans_out()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build("ACC-2025-A-12345678");
        var workItem = BuildWorkItem();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.True(result.IsSuccess);
        Assert.Equal("approved", workItem.StateId);
        Assert.Equal(s_fixedNow.UtcDateTime, workItem.LastModifiedAt);

        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        Assert.Equal("ACC-2025-A-12345678", payload.AccreditationId);
        Assert.Equal(DateOnly.FromDateTime(s_fixedNow.UtcDateTime), payload.AccreditationStartDate);
        Assert.Equal(2025, payload.AccreditationYear);
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
        Assert.Equal("ACC-2025-A-12345678", workItem.AuditLog[2].Details["accreditationId"]);
        Assert.Equal("2025", workItem.AuditLog[2].Details["accreditationYear"]);

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
        var sut = Build("ACC-2025-C-CAFEBABE");
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
            Arg.Is<Dictionary<string, string?>>(d => d["accreditationId"] == "ACC-2025-C-CAFEBABE"),
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
        await sut.Persistence.DidNotReceiveWithAnyArgs().GetByIdAsync(default, ct);
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
    public async Task Returns_InvalidTransition_and_does_not_persist_when_existing_payload_is_corrupt()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build("ACC-2025-A-AAAAAAAA");
        // Force the deserialiser to throw by stuffing a malformed value
        // into a typed field.
        var workItem = BuildWorkItem(payload: new BsonDocument
        {
            ["accreditationStartDate"] = "not-a-date"
        });
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        // A corrupt payload must NOT silently wipe existing data — the service
        // must abort and return a failure so the operator can investigate.
        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        // Persistence must not have been called — the corrupt item is left unchanged.
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        // The original payload should be untouched.
        Assert.Equal("not-a-date", workItem.Payload["accreditationStartDate"].AsString);
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
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => sut.Service.ApproveAsync(Guid.NewGuid(), user: null!, ct));
    }

    // ─────────────────────────── RA-133 ────────────────────────────────

    [Fact]
    public async Task ApproveAsync_passes_first_handled_material_and_configured_year_to_generator()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build(currentYear: 2028);
        var workItem = BuildWorkItem(payload: new BsonDocument
        {
            ["organisationName"] = "Acme Ltd",
            ["materialsHandled"] = new BsonArray { "plastic", "glass" }
        });
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        await sut.IdGenerator.Received(1).GenerateAsync(
            "plastic", 2028, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApproveAsync_passes_null_material_when_materialsHandled_is_missing_or_empty()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(payload: new BsonDocument
        {
            ["organisationName"] = "Acme Ltd",
            ["materialsHandled"] = new BsonArray()
        });
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        await sut.IdGenerator.Received(1).GenerateAsync(
            null, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApproveAsync_passes_null_material_when_first_entry_is_bson_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(payload: new BsonDocument
        {
            ["organisationName"] = "Acme Ltd",
            ["materialsHandled"] = new BsonArray { BsonNull.Value, "plastic" }
        });
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        await sut.IdGenerator.Received(1).GenerateAsync(
            null, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApproveAsync_uses_today_as_start_date_when_approval_is_after_jan_1_of_configured_year()
    {
        var ct = TestContext.Current.CancellationToken;
        // s_fixedNow = 2025-02-03; configured year 2025 → today > Jan 1.
        var sut = Build(currentYear: 2025);
        var workItem = BuildWorkItem();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        Assert.Equal(new DateOnly(2025, 2, 3), payload.AccreditationStartDate);
        Assert.Equal(2025, payload.AccreditationYear);
    }

    [Fact]
    public async Task ApproveAsync_uses_jan_1_as_start_date_when_approval_is_before_jan_1_of_configured_year()
    {
        var ct = TestContext.Current.CancellationToken;
        // s_fixedNow = 2025-02-03; configured year 2027 → Jan 1 of 2027 > today.
        var sut = Build(currentYear: 2027);
        var workItem = BuildWorkItem();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        Assert.Equal(new DateOnly(2027, 1, 1), payload.AccreditationStartDate);
        Assert.Equal(2027, payload.AccreditationYear);
    }

    [Fact]
    public async Task ApproveAsync_uses_jan_1_as_start_date_when_approval_is_exactly_on_jan_1_of_configured_year()
    {
        var ct = TestContext.Current.CancellationToken;
        var jan1 = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var sut = Build(currentYear: 2027, now: jan1);
        var workItem = BuildWorkItem();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        Assert.Equal(new DateOnly(2027, 1, 1), payload.AccreditationStartDate);
    }

    [Fact]
    public async Task ApproveAsync_is_idempotent_when_work_item_already_carries_an_accreditation_id_and_is_approved()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(stateId: "approved", payload: new BsonDocument
        {
            ["organisationName"] = "Acme Ltd",
            ["accreditationId"] = "ACC-2025-A-EXISTING"
        });
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.True(result.IsSuccess);
        // No re-stamping, no audit entries, no persistence, no fan-out.
        Assert.Empty(workItem.AuditLog);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        await sut.IdGenerator.DidNotReceiveWithAnyArgs().GenerateAsync(default, default, ct);
        await sut.Queue.DidNotReceiveWithAnyArgs().QueueAsync(default!, ct);
        await sut.Hooks[0].DidNotReceiveWithAnyArgs().OnActionAppliedAsync(
            default!, default!, default!, default!, ct);
    }

    [Fact]
    public async Task ApproveAsync_returns_InvalidTransition_when_accreditation_id_is_present_but_state_is_not_approved()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        var workItem = BuildWorkItem(stateId: "assessment-in-progress", payload: new BsonDocument
        {
            ["organisationName"] = "Acme Ltd",
            ["accreditationId"] = "ACC-2025-A-ORPHANED"
        });
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
        await sut.IdGenerator.DidNotReceiveWithAnyArgs().GenerateAsync(default, default, ct);
    }

    [Fact]
    public async Task ApproveAsync_returns_InvalidTransition_when_id_generator_exhausts_uniqueness_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Build();
        sut.IdGenerator.GenerateAsync(
                Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new InvalidOperationException("no unique id"));
        var workItem = BuildWorkItem();
        sut.Persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);

        var result = await sut.Service.ApproveAsync(workItem.Id, DecisionMaker(), ct);

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        await sut.Persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, ct);
    }
}
