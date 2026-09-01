using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Utils.Background;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.AspNetCore.HeaderPropagation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-368: pushes every re-accreditation state transition to the operator
/// backend, except the query/withdraw families which are out of scope (query
/// keeps its own richer push; withdrawal is a separate, future ticket).
/// Never throws — a push failure must not unwind the already-persisted
/// transition.
///
/// RA-519: the push (and its resulting audit entry) is no longer made
/// synchronously inline — it is deferred onto <see cref="IBackgroundTaskQueue"/>
/// so this hook (and the request it runs inside) never blocks on, or
/// re-enters, the operator backend's own webhook endpoint mid-request. Tests
/// below capture the delegate handed to <c>QueueAsync</c> and invoke it
/// directly (against a small <see cref="IServiceProvider"/> that resolves the
/// mocked adapter/audit-appender) to assert the same downstream behaviour the
/// synchronous version used to have.
/// </summary>
public class ReAccreditationStatusPushHookTests
{
    private static readonly ClaimsPrincipal s_user = new(
        new ClaimsIdentity([new Claim("user:id", "alice-1")], "test"));

    private static readonly DateTime s_lastModifiedAt = new(2026, 1, 15, 9, 30, 0, DateTimeKind.Utc);

    private static WorkItem BuildWorkItem(
        string stateId,
        string actionId,
        string actionDisplayName,
        string fromStateId,
        string typeId = ReAccreditationType.Id)
    {
        var workItem = new WorkItem
        {
            TypeId = typeId,
            StateId = stateId,
            LastModifiedAt = s_lastModifiedAt,
            Payload = new BsonDocument(),
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType()),
        };

        // Mirrors what every real caller (WorkItemService.ApplyActionAsync,
        // ReAccreditationApprovalService, ReAccreditationDulyMadeHook) already
        // appends immediately before invoking post-action hooks — the hook
        // reads actionDisplayName back off this entry.
        workItem.AuditLog.Add(new WorkItemAuditEntry
        {
            Action = "action-applied",
            ActionDisplayName = "Action applied",
            Details = new Dictionary<string, string?>
            {
                ["actionId"] = actionId,
                ["actionDisplayName"] = actionDisplayName,
                ["fromStateId"] = fromStateId,
                ["toStateId"] = stateId,
            },
            CreatedAt = s_lastModifiedAt,
        });

        return workItem;
    }

    private sealed record Sut(
        ReAccreditationStatusPushHook Hook,
        IBackgroundTaskQueue Queue,
        IOperatorBackendPushAdapter Adapter,
        IWorkItemAuditAppender AuditAppender);

    private static Sut BuildSut()
    {
        var adapter = Substitute.For<IOperatorBackendPushAdapter>();
        adapter
            .PushStatusChangedAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(OperatorBackendPushResult.Success());
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        auditAppender
            .AppendAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var queue = Substitute.For<IBackgroundTaskQueue>();

        var hook = new ReAccreditationStatusPushHook(
            queue, new HeaderPropagationValues(), NullLogger<ReAccreditationStatusPushHook>.Instance);
        return new Sut(hook, queue, adapter, auditAppender);
    }

    /// <summary>
    /// Builds a scope-like <see cref="IServiceProvider"/> resolving the sut's
    /// mocked adapter/audit-appender, the way <c>QueuedHostedService</c>'s
    /// fresh DI scope would for the real ones.
    /// </summary>
    private static ServiceProvider BuildScopedServices(Sut sut)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sut.Adapter);
        services.AddSingleton(sut.AuditAppender);
        services.AddSingleton(new HeaderPropagationValues());
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Captures the delegate passed to <c>Queue.QueueAsync</c> during
    /// <paramref name="act"/> and runs it against a scope resolving the sut's
    /// mocked adapter/audit-appender — i.e. what would eventually happen on
    /// the background worker.
    /// </summary>
    private static async Task ActAndRunQueuedJobAsync(
        Sut sut, Func<Task> act, CancellationToken ct)
    {
        Func<IServiceProvider, CancellationToken, Task>? captured = null;
        sut.Queue
            .QueueAsync(
                Arg.Do<Func<IServiceProvider, CancellationToken, Task>>(j => captured = j),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await act();

        Assert.NotNull(captured);
        await using var services = BuildScopedServices(sut);
        await captured!(services, ct);
    }

    [Theory]
    [InlineData("payment-received", "assessment-in-progress", "duly-made")]
    [InlineData("sla-extend", "assessment-in-progress", "assessment-in-progress")]
    // RA-351: the queried sla-extend self-loop is a status change, not a move
    // onto queried, so it is pushed — sharing the action id with the
    // assessment self-loop must not drag it into the excluded set.
    [InlineData("sla-extend", "queried", "queried")]
    [InlineData("resume-during-duly-making", "updated", "queried")]
    [InlineData("continue-review-during-duly-making", "submitted", "updated")]
    public async Task OnActionAppliedAsync_pushes_status_for_non_excluded_actions(
        string actionId, string toStateId, string fromStateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = BuildSut();
        var workItem = BuildWorkItem(toStateId, actionId, "Some action", fromStateId);

        await ActAndRunQueuedJobAsync(
            sut, () => sut.Hook.OnActionAppliedAsync(workItem, actionId, fromStateId, s_user, ct), ct);

        await sut.Adapter.Received(1).PushStatusChangedAsync(
            workItem.Id, Arg.Any<Guid>(), fromStateId, toStateId, Arg.Any<string>(),
            actionId, Arg.Any<string>(), s_lastModifiedAt, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_defers_the_push_via_the_background_queue_rather_than_calling_it_inline()
    {
        // RA-519 regression test: the whole point of the fix is that this
        // hook must return without having called the adapter — it must only
        // have handed a job to the background queue. Calling the adapter
        // inline here would mean this hook is once again re-entering the
        // operator backend's own webhook endpoint synchronously, inside the
        // request that endpoint itself initiated.
        var ct = TestContext.Current.CancellationToken;
        var sut = BuildSut();
        var workItem = BuildWorkItem("duly-made", "duly-make", "Mark as duly made", "submitted");

        await sut.Hook.OnActionAppliedAsync(workItem, "duly-make", "submitted", s_user, ct);

        await sut.Queue.Received(1).QueueAsync(
            Arg.Any<Func<IServiceProvider, CancellationToken, Task>>(), ct);
        await sut.Adapter.DidNotReceiveWithAnyArgs().PushStatusChangedAsync(
            default, default, default!, default!, default!, default!, default!, default, ct);
    }

    [Theory]
    [InlineData("submit-for-decision", "awaiting-decision", "assessment-in-progress")]
    [InlineData("approve", "approved", "awaiting-decision")]
    [InlineData("reject", "rejected", "awaiting-decision")]
    public async Task OnActionAppliedAsync_ignores_the_decision_actions(
        string actionId, string toStateId, string fromStateId)
    {
        // epr-p86e / RA-410: the three decision actions are now owned by
        // ReAccreditationLogDecisionService, which fires the operator-journey
        // push ONCE as a pre-commit gate. This hook must NOT push for any of
        // them, or the item double-pushes (submit-for-decision + approve/reject)
        // — the bug that stranded applications in 'awaiting-decision' when the
        // operator journey was down.
        var ct = TestContext.Current.CancellationToken;
        var sut = BuildSut();
        var workItem = BuildWorkItem(toStateId, actionId, "Some action", fromStateId);

        await sut.Hook.OnActionAppliedAsync(workItem, actionId, fromStateId, s_user, ct);

        await sut.Queue.DidNotReceiveWithAnyArgs().QueueAsync(default!, ct);
    }

    [Theory]
    [InlineData("query-during-duly-making")]
    [InlineData("query-during-duly-made")]
    [InlineData("query-during-assessment")]
    [InlineData("query-during-decision")]
    [InlineData("withdraw")]
    [InlineData("withdraw-during-duly-made")]
    [InlineData("withdraw-during-assessment")]
    [InlineData("withdraw-during-decision")]
    [InlineData("withdraw-during-query")]
    [InlineData("withdraw-during-updated")]
    public async Task OnActionAppliedAsync_ignores_query_and_withdraw_actions(string actionId)
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = BuildSut();
        var workItem = BuildWorkItem("queried", actionId, "Some action", "submitted");

        await sut.Hook.OnActionAppliedAsync(workItem, actionId, "submitted", s_user, ct);

        await sut.Queue.DidNotReceiveWithAnyArgs().QueueAsync(default!, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_ignores_a_work_item_of_a_different_type()
    {
        // Mirrors ReAccreditationQueryPushHook's own type guard — this hook
        // must not fire for a work item type it doesn't own, or it would
        // POST to the re-accreditation-specific status endpoint for every
        // other module's transitions too.
        var ct = TestContext.Current.CancellationToken;
        var sut = BuildSut();
        // A non-excluded action, so the guard under test is the TYPE guard —
        // not the epr-p86e decision-action exclusion.
        var workItem = BuildWorkItem(
            "duly-made", "duly-make", "Mark as duly made", "submitted", typeId: "some-other-type");

        await sut.Hook.OnActionAppliedAsync(workItem, "duly-make", "submitted", s_user, ct);

        await sut.Queue.DidNotReceiveWithAnyArgs().QueueAsync(default!, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_resolves_toStateDisplayName_from_the_template_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = BuildSut();
        var workItem = BuildWorkItem("duly-made", "duly-make", "Mark as duly made", "submitted");

        await ActAndRunQueuedJobAsync(
            sut, () => sut.Hook.OnActionAppliedAsync(workItem, "duly-make", "submitted", s_user, ct), ct);

        await sut.Adapter.Received(1).PushStatusChangedAsync(
            workItem.Id, Arg.Any<Guid>(), "submitted", "duly-made", "Duly made",
            "duly-make", "Mark as duly made", s_lastModifiedAt, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_resolves_actionDisplayName_from_the_action_applied_audit_entry()
    {
        // "duly-make" and "approve" have no declared WorkItemTransition (both
        // are applied outside the generic engine's transition set), so the
        // action-applied audit entry the caller already appended is the only
        // source of a human-readable label.
        var ct = TestContext.Current.CancellationToken;
        var sut = BuildSut();
        var workItem = BuildWorkItem("duly-made", "duly-make", "Mark as duly made", "submitted");

        await ActAndRunQueuedJobAsync(
            sut, () => sut.Hook.OnActionAppliedAsync(workItem, "duly-make", "submitted", s_user, ct), ct);

        await sut.Adapter.Received(1).PushStatusChangedAsync(
            workItem.Id, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), "Mark as duly made", Arg.Any<DateTime>(), ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_falls_back_to_the_raw_ids_when_no_audit_entry_or_template_match()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = BuildSut();
        var workItem = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "some-unknown-state",
            LastModifiedAt = s_lastModifiedAt,
            Payload = new BsonDocument(),
        };

        await ActAndRunQueuedJobAsync(
            sut, () => sut.Hook.OnActionAppliedAsync(workItem, "some-unknown-action", "submitted", s_user, ct), ct);

        await sut.Adapter.Received(1).PushStatusChangedAsync(
            workItem.Id, Arg.Any<Guid>(), "submitted", "some-unknown-state", "some-unknown-state",
            "some-unknown-action", "some-unknown-action", s_lastModifiedAt, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_records_a_sent_audit_entry_with_actionDisplayName_and_toStateDisplayName()
    {
        // management-fe's audit-log projection (statusPushDetailRows /
        // summariseAuditEntry) reads details.actionDisplayName and
        // details.toStateDisplayName, falling back to the raw ids only when
        // absent — both must be present or a caseworker sees wire ids
        // instead of "Approve" / "Granted" in the audit trail.
        var ct = TestContext.Current.CancellationToken;
        var sut = BuildSut();
        var workItem = BuildWorkItem("duly-made", "duly-make", "Mark as duly made", "submitted");

        await ActAndRunQueuedJobAsync(
            sut, () => sut.Hook.OnActionAppliedAsync(workItem, "duly-make", "submitted", s_user, ct), ct);

        await sut.AuditAppender.Received(1).AppendAsync(
            workItem.Id, "status-push-sent", "Status sent to the Registration & Accreditation service",
            Arg.Is<Dictionary<string, string?>>(d =>
                d["actionDisplayName"] == "Mark as duly made" && d["toStateDisplayName"] == "Duly made"),
            s_user, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_records_a_skipped_audit_entry_when_the_push_is_disabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = BuildSut();
        sut.Adapter
            .PushStatusChangedAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(OperatorBackendPushResult.Skipped("OperatorBackendApi:Enabled is false."));
        var workItem = BuildWorkItem("duly-made", "duly-make", "Mark as duly made", "submitted");

        await ActAndRunQueuedJobAsync(
            sut, () => sut.Hook.OnActionAppliedAsync(workItem, "duly-make", "submitted", s_user, ct), ct);

        // MBE-F5: skipped (deliberately disabled) must never look like a
        // failure — a distinct audit outcome, not status-push-failed.
        await sut.AuditAppender.Received(1).AppendAsync(
            workItem.Id, "status-push-skipped", "Status not sent to the Registration & Accreditation service (disabled)",
            Arg.Any<Dictionary<string, string?>>(), s_user, ct);
        await sut.AuditAppender.DidNotReceive().AppendAsync(
            workItem.Id, "status-push-failed", Arg.Any<string>(),
            Arg.Any<Dictionary<string, string?>>(), s_user, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_records_a_failed_audit_entry_when_the_push_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = BuildSut();
        sut.Adapter
            .PushStatusChangedAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(OperatorBackendPushResult.Failure("connection refused"));
        var workItem = BuildWorkItem("duly-made", "duly-make", "Mark as duly made", "submitted");

        await ActAndRunQueuedJobAsync(
            sut, () => sut.Hook.OnActionAppliedAsync(workItem, "duly-make", "submitted", s_user, ct), ct);

        await sut.AuditAppender.Received(1).AppendAsync(
            workItem.Id, "status-push-failed", "Status failed to send to the Registration & Accreditation service",
            Arg.Is<Dictionary<string, string?>>(d => d["errorMessage"] == "connection refused"),
            s_user, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_includes_the_same_correlation_id_in_the_push_call_and_the_sent_audit_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = BuildSut();
        var workItem = BuildWorkItem("duly-made", "duly-make", "Mark as duly made", "submitted");
        Guid? correlationIdPassedToAdapter = null;
        sut.Adapter
            .PushStatusChangedAsync(
                Arg.Any<Guid>(), Arg.Do<Guid>(id => correlationIdPassedToAdapter = id),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(OperatorBackendPushResult.Success());

        await ActAndRunQueuedJobAsync(
            sut, () => sut.Hook.OnActionAppliedAsync(workItem, "duly-make", "submitted", s_user, ct), ct);

        Assert.NotNull(correlationIdPassedToAdapter);
        await sut.AuditAppender.Received(1).AppendAsync(
            workItem.Id, "status-push-sent", Arg.Any<string>(),
            Arg.Is<Dictionary<string, string?>>(d =>
                d.GetValueOrDefault("correlationId") == correlationIdPassedToAdapter.ToString()),
            s_user, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_never_throws_when_the_adapter_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = BuildSut();
        sut.Adapter
            .PushStatusChangedAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns<OperatorBackendPushResult>(_ => throw new InvalidOperationException("boom"));
        var workItem = BuildWorkItem("duly-made", "duly-make", "Mark as duly made", "submitted");

        Func<IServiceProvider, CancellationToken, Task>? captured = null;
        sut.Queue
            .QueueAsync(
                Arg.Do<Func<IServiceProvider, CancellationToken, Task>>(j => captured = j),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Should not throw — the synchronous hook call only enqueues, so it
        // can't observe the adapter throwing; the queued job itself (what
        // would run on the background worker) must swallow it independently.
        await sut.Hook.OnActionAppliedAsync(workItem, "duly-make", "submitted", s_user, ct);

        Assert.NotNull(captured);
        await using var services = BuildScopedServices(sut);
        await captured!(services, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_never_throws_when_queueing_itself_fails()
    {
        // RA-519: a failure to even hand the job to the queue (e.g. the
        // bounded channel rejecting it) must not unwind the already-persisted
        // transition either — the same never-throws contract as a push
        // failure used to guarantee.
        var ct = TestContext.Current.CancellationToken;
        var sut = BuildSut();
        sut.Queue
            .QueueAsync(
                Arg.Any<Func<IServiceProvider, CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("queue full"));
        var workItem = BuildWorkItem("duly-made", "duly-make", "Mark as duly made", "submitted");

        var exception = await Record.ExceptionAsync(
            () => sut.Hook.OnActionAppliedAsync(workItem, "duly-make", "submitted", s_user, ct));

        Assert.Null(exception);
    }
}
