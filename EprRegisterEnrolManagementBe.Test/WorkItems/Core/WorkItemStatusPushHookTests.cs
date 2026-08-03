using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-368: pushes every work item state transition to the operator backend,
/// except the query/withdraw families which are out of scope (query keeps
/// its own richer push; withdrawal is a separate, future ticket). Never
/// throws — a push failure must not unwind the already-persisted transition.
/// </summary>
public class WorkItemStatusPushHookTests
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

    private static (WorkItemStatusPushHook Hook, IOperatorBackendPushAdapter Adapter, IWorkItemAuditAppender AuditAppender)
        BuildSut()
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

        var registry = new WorkItemRegistry([new ReAccreditationType()]);
        var hook = new WorkItemStatusPushHook(
            adapter, registry, auditAppender, NullLogger<WorkItemStatusPushHook>.Instance);
        return (hook, adapter, auditAppender);
    }

    [Theory]
    [InlineData("payment-received", "assessment-in-progress", "duly-made")]
    [InlineData("sla-extend", "assessment-in-progress", "assessment-in-progress")]
    [InlineData("submit-for-decision", "awaiting-decision", "assessment-in-progress")]
    [InlineData("reject", "rejected", "awaiting-decision")]
    [InlineData("approve", "approved", "awaiting-decision")]
    [InlineData("resume-during-duly-making", "updated", "queried")]
    [InlineData("continue-review-during-duly-making", "submitted", "updated")]
    public async Task OnActionAppliedAsync_pushes_status_for_non_excluded_actions(
        string actionId, string toStateId, string fromStateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var (hook, adapter, _) = BuildSut();
        var workItem = BuildWorkItem(toStateId, actionId, "Some action", fromStateId);

        await hook.OnActionAppliedAsync(workItem, actionId, fromStateId, s_user, ct);

        await adapter.Received(1).PushStatusChangedAsync(
            workItem.Id, Arg.Any<Guid>(), fromStateId, toStateId, Arg.Any<string>(),
            actionId, Arg.Any<string>(), s_lastModifiedAt, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_pushing_approve_reaches_the_adapter()
    {
        // RA-368 §5 explicit requirement: approving a re-accreditation must
        // fire the status push, even though "approve" is handled by the
        // bespoke ReAccreditationApprovalService rather than the generic
        // engine — the hook itself has no special case for it.
        var ct = TestContext.Current.CancellationToken;
        var (hook, adapter, _) = BuildSut();
        var workItem = BuildWorkItem("approved", "approve", "Approve", "awaiting-decision");

        await hook.OnActionAppliedAsync(workItem, "approve", "awaiting-decision", s_user, ct);

        await adapter.Received(1).PushStatusChangedAsync(
            workItem.Id, Arg.Any<Guid>(), "awaiting-decision", "approved", "Granted",
            "approve", "Approve", s_lastModifiedAt, ct);
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
        var (hook, adapter, _) = BuildSut();
        var workItem = BuildWorkItem("queried", actionId, "Some action", "submitted");

        await hook.OnActionAppliedAsync(workItem, actionId, "submitted", s_user, ct);

        await adapter.DidNotReceiveWithAnyArgs().PushStatusChangedAsync(
            default, default, default!, default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task OnActionAppliedAsync_resolves_toStateDisplayName_from_the_template_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var (hook, adapter, _) = BuildSut();
        var workItem = BuildWorkItem("duly-made", "duly-make", "Mark as duly made", "submitted");

        await hook.OnActionAppliedAsync(workItem, "duly-make", "submitted", s_user, ct);

        await adapter.Received(1).PushStatusChangedAsync(
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
        var (hook, adapter, _) = BuildSut();
        var workItem = BuildWorkItem("duly-made", "duly-make", "Mark as duly made", "submitted");

        await hook.OnActionAppliedAsync(workItem, "duly-make", "submitted", s_user, ct);

        await adapter.Received(1).PushStatusChangedAsync(
            workItem.Id, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), "Mark as duly made", Arg.Any<DateTime>(), ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_falls_back_to_the_raw_ids_when_no_audit_entry_or_template_match()
    {
        var ct = TestContext.Current.CancellationToken;
        var (hook, adapter, _) = BuildSut();
        var workItem = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "some-unknown-state",
            LastModifiedAt = s_lastModifiedAt,
            Payload = new BsonDocument(),
        };

        await hook.OnActionAppliedAsync(workItem, "some-unknown-action", "submitted", s_user, ct);

        await adapter.Received(1).PushStatusChangedAsync(
            workItem.Id, Arg.Any<Guid>(), "submitted", "some-unknown-state", "some-unknown-state",
            "some-unknown-action", "some-unknown-action", s_lastModifiedAt, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_records_a_sent_audit_entry_on_success()
    {
        var ct = TestContext.Current.CancellationToken;
        var (hook, _, auditAppender) = BuildSut();
        var workItem = BuildWorkItem("rejected", "reject", "Reject", "awaiting-decision");

        await hook.OnActionAppliedAsync(workItem, "reject", "awaiting-decision", s_user, ct);

        await auditAppender.Received(1).AppendAsync(
            workItem.Id, "status-push-sent", Arg.Any<string>(),
            Arg.Any<Dictionary<string, string?>>(), s_user, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_records_a_skipped_audit_entry_when_the_push_is_disabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var (hook, adapter, auditAppender) = BuildSut();
        adapter
            .PushStatusChangedAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(OperatorBackendPushResult.Skipped("OperatorBackendApi:Enabled is false."));
        var workItem = BuildWorkItem("rejected", "reject", "Reject", "awaiting-decision");

        await hook.OnActionAppliedAsync(workItem, "reject", "awaiting-decision", s_user, ct);

        // MBE-F5: skipped (deliberately disabled) must never look like a
        // failure — a distinct audit outcome, not status-push-failed.
        await auditAppender.Received(1).AppendAsync(
            workItem.Id, "status-push-skipped", Arg.Any<string>(),
            Arg.Any<Dictionary<string, string?>>(), s_user, ct);
        await auditAppender.DidNotReceive().AppendAsync(
            workItem.Id, "status-push-failed", Arg.Any<string>(),
            Arg.Any<Dictionary<string, string?>>(), s_user, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_records_a_failed_audit_entry_when_the_push_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var (hook, adapter, auditAppender) = BuildSut();
        adapter
            .PushStatusChangedAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(OperatorBackendPushResult.Failure("connection refused"));
        var workItem = BuildWorkItem("rejected", "reject", "Reject", "awaiting-decision");

        await hook.OnActionAppliedAsync(workItem, "reject", "awaiting-decision", s_user, ct);

        await auditAppender.Received(1).AppendAsync(
            workItem.Id, "status-push-failed", Arg.Any<string>(),
            Arg.Is<Dictionary<string, string?>>(d => d["errorMessage"] == "connection refused"),
            s_user, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_includes_the_same_correlation_id_in_the_push_call_and_the_sent_audit_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var (hook, adapter, auditAppender) = BuildSut();
        var workItem = BuildWorkItem("rejected", "reject", "Reject", "awaiting-decision");
        Guid? correlationIdPassedToAdapter = null;
        adapter
            .PushStatusChangedAsync(
                Arg.Any<Guid>(), Arg.Do<Guid>(id => correlationIdPassedToAdapter = id),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(OperatorBackendPushResult.Success());

        await hook.OnActionAppliedAsync(workItem, "reject", "awaiting-decision", s_user, ct);

        Assert.NotNull(correlationIdPassedToAdapter);
        await auditAppender.Received(1).AppendAsync(
            workItem.Id, "status-push-sent", Arg.Any<string>(),
            Arg.Is<Dictionary<string, string?>>(d =>
                d.GetValueOrDefault("correlationId") == correlationIdPassedToAdapter.ToString()),
            s_user, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_never_throws_when_the_adapter_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var (hook, adapter, _) = BuildSut();
        adapter
            .PushStatusChangedAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns<OperatorBackendPushResult>(_ => throw new InvalidOperationException("boom"));
        var workItem = BuildWorkItem("rejected", "reject", "Reject", "awaiting-decision");

        // Should not throw.
        await hook.OnActionAppliedAsync(workItem, "reject", "awaiting-decision", s_user, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_does_not_filter_by_work_item_type()
    {
        // Unlike ReAccreditationQueryPushHook, this hook is framework-level
        // (WorkItems/Core) — it fires for every work item type, not just
        // re-accreditation. Only the excluded action-id families (query/
        // withdraw) and an unconfigured/disabled adapter suppress a push.
        var ct = TestContext.Current.CancellationToken;
        var (hook, adapter, _) = BuildSut();
        var workItem = BuildWorkItem(
            "rejected", "reject", "Reject", "awaiting-decision", typeId: "some-other-type");

        await hook.OnActionAppliedAsync(workItem, "reject", "awaiting-decision", s_user, ct);

        await adapter.Received(1).PushStatusChangedAsync(
            workItem.Id, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), ct);
    }
}
