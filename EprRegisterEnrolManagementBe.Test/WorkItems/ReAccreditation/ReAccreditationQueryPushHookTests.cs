using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.Test.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-311/MBE-1: pushes the query note + sections to the operator backend on
/// each of the four query-during-* actions. Never throws — a push failure
/// must not unwind the already-persisted query transition.
/// </summary>
public class ReAccreditationQueryPushHookTests
{
    private static readonly ClaimsPrincipal s_user = new(
        new ClaimsIdentity([new Claim("user:id", "alice-1")], "test"));

    private const string DefaultActionId = "query-during-duly-making";

    /// <summary>
    /// A queried work item as the engine hands it to the hook after
    /// ReAccreditationQueryService stamped its query and applied
    /// <paramref name="actionId"/>. <paramref name="currentQuery"/> overrides
    /// the fresh stamp; <paramref name="omitCurrentQuery"/> leaves none.
    /// </summary>
    private static WorkItem BuildWorkItem(
        string typeId = ReAccreditationType.Id,
        string actionId = DefaultActionId,
        BsonDocument? currentQuery = null,
        bool omitCurrentQuery = false)
    {
        var payload = new BsonDocument
        {
            ["organisationName"] = "Acme Ltd",
        };
        if (!omitCurrentQuery)
        {
            payload["currentQuery"] = currentQuery ?? FreshQueryTestData.CurrentQuery();
        }

        var workItem = new WorkItem
        {
            TypeId = typeId,
            StateId = "queried",
            Payload = payload,
            SubmittedAt = FreshQueryTestData.SubmittedAt,
        };
        FreshQueryTestData.AppendQueryTransition(workItem, actionId, FreshQueryTestData.FromStateFor(actionId));
        return workItem;
    }

    private static (ReAccreditationQueryPushHook Hook, IOperatorBackendPushAdapter Adapter, IWorkItemAuditAppender AuditAppender)
        BuildSut(ILogger<ReAccreditationQueryPushHook>? logger = null)
    {
        var adapter = Substitute.For<IOperatorBackendPushAdapter>();
        adapter
            .PushQueryRaisedAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(OperatorBackendPushResult.Success());
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        auditAppender
            .AppendAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var hook = new ReAccreditationQueryPushHook(
            adapter, auditAppender, logger ?? NullLogger<ReAccreditationQueryPushHook>.Instance);
        return (hook, adapter, auditAppender);
    }

    [Theory]
    [InlineData("query-during-duly-making")]
    [InlineData("query-during-duly-made")]
    [InlineData("query-during-assessment")]
    [InlineData("query-during-decision")]
    public async Task OnActionAppliedAsync_pushes_the_query_note_and_sections(string actionId)
    {
        var ct = TestContext.Current.CancellationToken;
        var (hook, adapter, _) = BuildSut();
        var workItem = BuildWorkItem(actionId: actionId);

        await hook.OnActionAppliedAsync(workItem, actionId, FreshQueryTestData.FromStateFor(actionId), s_user, ct);

        var expectedSections = new[] { "business-plan", "prn-tonnage" };
        await adapter.Received(1).PushQueryRaisedAsync(
            workItem.Id,
            Arg.Any<Guid>(),
            "Tonnage does not reconcile",
            Arg.Is<IReadOnlyList<string>>(s => s.SequenceEqual(expectedSections)),
            ct);
    }

    [Theory]
    [InlineData("submit-for-decision")]
    [InlineData("start-assessment")]
    [InlineData("withdraw")]
    [InlineData("approve")]
    public async Task OnActionAppliedAsync_ignores_non_query_actions(string actionId)
    {
        var ct = TestContext.Current.CancellationToken;
        var (hook, adapter, _) = BuildSut();
        var workItem = BuildWorkItem();

        await hook.OnActionAppliedAsync(workItem, actionId, "submitted", s_user, ct);

        await adapter.DidNotReceiveWithAnyArgs()
            .PushQueryRaisedAsync(default, default, default!, default!, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_ignores_a_work_item_of_a_different_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var (hook, adapter, _) = BuildSut();
        var workItem = BuildWorkItem(typeId: "some-other-type");

        await hook.OnActionAppliedAsync(workItem, "query-during-duly-making", "submitted", s_user, ct);

        await adapter.DidNotReceiveWithAnyArgs()
            .PushQueryRaisedAsync(default, default, default!, default!, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_records_a_sent_audit_entry_on_success()
    {
        var ct = TestContext.Current.CancellationToken;
        var (hook, _, auditAppender) = BuildSut();
        var workItem = BuildWorkItem();

        await hook.OnActionAppliedAsync(workItem, "query-during-duly-making", "submitted", s_user, ct);

        await auditAppender.Received(1).AppendAsync(
            workItem.Id, "query-push-sent", Arg.Any<string>(),
            Arg.Any<Dictionary<string, string?>>(), s_user, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_logs_a_warning_when_the_sent_audit_entry_fails_to_persist()
    {
        // The push itself succeeded; only the follow-up audit-append failed
        // (e.g. a concurrent mutation). Must not throw.
        var ct = TestContext.Current.CancellationToken;
        var (hook, _, auditAppender) = BuildSut();
        auditAppender
            .AppendAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var workItem = BuildWorkItem();

        var exception = await Record.ExceptionAsync(
            () => hook.OnActionAppliedAsync(workItem, "query-during-duly-making", "submitted", s_user, ct));

        Assert.Null(exception);
    }

    [Fact]
    public async Task OnActionAppliedAsync_records_a_skipped_audit_entry_when_the_push_is_disabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var (hook, adapter, auditAppender) = BuildSut();
        adapter
            .PushQueryRaisedAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(OperatorBackendPushResult.Skipped("OperatorBackendApi:Enabled is false."));
        var workItem = BuildWorkItem();

        await hook.OnActionAppliedAsync(workItem, "query-during-duly-making", "submitted", s_user, ct);

        // MBE-F5: skipped (deliberately disabled) must never look like a
        // failure — a distinct audit outcome, not query-push-failed.
        await auditAppender.Received(1).AppendAsync(
            workItem.Id, "query-push-skipped", Arg.Any<string>(),
            Arg.Any<Dictionary<string, string?>>(), s_user, ct);
        await auditAppender.DidNotReceive().AppendAsync(
            workItem.Id, "query-push-failed", Arg.Any<string>(),
            Arg.Any<Dictionary<string, string?>>(), s_user, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_records_a_failed_audit_entry_when_the_push_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var (hook, adapter, auditAppender) = BuildSut();
        adapter
            .PushQueryRaisedAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(OperatorBackendPushResult.Failure("connection refused"));
        var workItem = BuildWorkItem();

        await hook.OnActionAppliedAsync(workItem, "query-during-duly-making", "submitted", s_user, ct);

        await auditAppender.Received(1).AppendAsync(
            workItem.Id, "query-push-failed", Arg.Any<string>(),
            Arg.Is<Dictionary<string, string?>>(d => d["errorMessage"] == "connection refused"),
            s_user, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_includes_the_same_correlation_id_in_the_push_call_and_the_sent_audit_entry()
    {
        // RA-311/MBE-1 cross-repo contract: one correlation id generated per
        // push, passed to the adapter AND recorded in the audit trail, so
        // this service's own record of a successful push can be joined back
        // to the operator backend's logs for the same request.
        var ct = TestContext.Current.CancellationToken;
        var (hook, adapter, auditAppender) = BuildSut();
        var workItem = BuildWorkItem();
        Guid? correlationIdPassedToAdapter = null;
        adapter
            .PushQueryRaisedAsync(
                Arg.Any<Guid>(), Arg.Do<Guid>(id => correlationIdPassedToAdapter = id),
                Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(OperatorBackendPushResult.Success());

        await hook.OnActionAppliedAsync(workItem, "query-during-duly-making", "submitted", s_user, ct);

        Assert.NotNull(correlationIdPassedToAdapter);
        await auditAppender.Received(1).AppendAsync(
            workItem.Id, "query-push-sent", Arg.Any<string>(),
            Arg.Is<Dictionary<string, string?>>(d =>
                d.GetValueOrDefault("correlationId") == correlationIdPassedToAdapter.ToString()),
            s_user, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_includes_the_same_correlation_id_in_the_push_call_and_the_failed_audit_entry()
    {
        // Same contract as the success case above, but for a failed push:
        // the query-push-failed audit entry must carry the exact
        // correlation id the adapter was called with, so a failure can be
        // traced back to the operator backend's own logs for that attempt.
        var ct = TestContext.Current.CancellationToken;
        var (hook, adapter, auditAppender) = BuildSut();
        var workItem = BuildWorkItem();
        Guid? correlationIdPassedToAdapter = null;
        adapter
            .PushQueryRaisedAsync(
                Arg.Any<Guid>(), Arg.Do<Guid>(id => correlationIdPassedToAdapter = id),
                Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(OperatorBackendPushResult.Failure("connection refused"));

        await hook.OnActionAppliedAsync(workItem, "query-during-duly-making", "submitted", s_user, ct);

        Assert.NotNull(correlationIdPassedToAdapter);
        await auditAppender.Received(1).AppendAsync(
            workItem.Id, "query-push-failed", Arg.Any<string>(),
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
            .PushQueryRaisedAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<OperatorBackendPushResult>(_ => throw new InvalidOperationException("boom"));
        var workItem = BuildWorkItem();

        // Should not throw.
        await hook.OnActionAppliedAsync(workItem, "query-during-duly-making", "submitted", s_user, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_does_not_push_an_unparseable_payload_and_records_the_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new ListLogger<ReAccreditationQueryPushHook>();
        var (hook, adapter, auditAppender) = BuildSut(logger);
        var workItem = BuildWorkItem();
        // currentQuery modelled as a scalar instead of a document —
        // BsonSerializer.Deserialize<ReAccreditationPayload> throws.
        workItem.Payload!["currentQuery"] = "not-a-document";

        var exception = await Record.ExceptionAsync(
            () => hook.OnActionAppliedAsync(workItem, DefaultActionId, "submitted", s_user, ct));

        Assert.Null(exception);
        await adapter.DidNotReceiveWithAnyArgs()
            .PushQueryRaisedAsync(default, default, default!, default!, ct);
        await auditAppender.Received(1).AppendAsync(
            workItem.Id, "query-push-failed", Arg.Any<string>(),
            Arg.Is<Dictionary<string, string?>>(d =>
                d["reason"] == "payload could not be deserialised"
                && d["actionId"] == DefaultActionId
                && !string.IsNullOrEmpty(d["correlationId"])),
            s_user, ct);
        var error = Assert.Single(logger.Records, r => r.Level == LogLevel.Error);
        Assert.NotNull(error.Exception);
        Assert.Equal(workItem.Id, error.Properties["WorkItemId"]);
        Assert.IsType<Guid>(error.Properties["CorrelationId"]);
        // RA-000: the old text claimed the push "will be skipped" and then
        // pushed an empty query anyway.
        Assert.DoesNotContain("will be skipped", error.Message);
        Assert.Contains("not attempted", error.Message);
    }

    [Fact]
    public async Task OnActionAppliedAsync_does_not_push_when_there_is_no_current_query_and_records_the_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new ListLogger<ReAccreditationQueryPushHook>();
        var (hook, adapter, auditAppender) = BuildSut(logger);
        var workItem = BuildWorkItem(omitCurrentQuery: true);

        await hook.OnActionAppliedAsync(workItem, DefaultActionId, "submitted", s_user, ct);

        await adapter.DidNotReceiveWithAnyArgs()
            .PushQueryRaisedAsync(default, default, default!, default!, ct);
        await auditAppender.Received(1).AppendAsync(
            workItem.Id, "query-push-failed", Arg.Any<string>(),
            Arg.Is<Dictionary<string, string?>>(d => d["reason"] == "payload.currentQuery is missing"),
            s_user, ct);
        var details = (Dictionary<string, string?>)auditAppender.ReceivedCalls().Single().GetArguments()[3]!;
        var correlationId = Guid.Parse(details["correlationId"]!);
        var error = Assert.Single(logger.Records, r => r.Level == LogLevel.Error);
        Assert.Equal(workItem.Id, error.Properties["WorkItemId"]);
        Assert.Equal(correlationId, error.Properties["CorrelationId"]);
        Assert.Equal("payload.currentQuery is missing", error.Properties["Reason"]);
    }

    [Fact]
    public async Task OnActionAppliedAsync_does_not_push_a_stale_current_query_left_by_an_earlier_query()
    {
        // The previous query's stamp, which carries that query's id rather
        // than the one on this transition. Pushing it would ask the operator
        // the old question again.
        var ct = TestContext.Current.CancellationToken;
        var logger = new ListLogger<ReAccreditationQueryPushHook>();
        var (hook, adapter, auditAppender) = BuildSut(logger);
        var workItem = BuildWorkItem(
            actionId: "query-during-duly-made",
            currentQuery: FreshQueryTestData.CurrentQuery(queryId: FreshQueryTestData.StaleQueryId));

        await hook.OnActionAppliedAsync(workItem, "query-during-duly-made", "duly-made", s_user, ct);

        await adapter.DidNotReceiveWithAnyArgs()
            .PushQueryRaisedAsync(default, default, default!, default!, ct);
        await auditAppender.Received(1).AppendAsync(
            workItem.Id, "query-push-failed", Arg.Any<string>(),
            Arg.Is<Dictionary<string, string?>>(d =>
                d["reason"]!.Contains(
                    $"payload.currentQuery is query {FreshQueryTestData.StaleQueryId}, but the "
                    + $"query-during-duly-made transition was applied for query {FreshQueryTestData.QueryId}")),
            s_user, ct);
        var error = Assert.Single(logger.Records, r => r.Level == LogLevel.Error);
        Assert.Equal(workItem.Id, error.Properties["WorkItemId"]);
        Assert.IsType<Guid>(error.Properties["CorrelationId"]);
    }

    [Fact]
    public async Task OnActionAppliedAsync_logs_at_error_when_the_not_attempted_failure_cannot_be_persisted()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new ListLogger<ReAccreditationQueryPushHook>();
        var (hook, adapter, auditAppender) = BuildSut(logger);
        auditAppender
            .AppendAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var workItem = BuildWorkItem(omitCurrentQuery: true);

        var exception = await Record.ExceptionAsync(
            () => hook.OnActionAppliedAsync(workItem, DefaultActionId, "submitted", s_user, ct));

        Assert.Null(exception);
        await adapter.DidNotReceiveWithAnyArgs()
            .PushQueryRaisedAsync(default, default, default!, default!, ct);
        // Both the not-attempted reason and the lost failure record are errors.
        Assert.Equal(2, logger.Records.Count(r => r.Level == LogLevel.Error));
        Assert.Contains(logger.Records, r => r.Level == LogLevel.Error
            && r.Message.Contains("query-push-failed audit entry could not be persisted"));
    }

    [Fact]
    public async Task OnActionAppliedAsync_logs_at_error_when_the_rejected_push_failure_cannot_be_persisted()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new ListLogger<ReAccreditationQueryPushHook>();
        var (hook, adapter, auditAppender) = BuildSut(logger);
        adapter
            .PushQueryRaisedAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(OperatorBackendPushResult.Failure("connection refused"));
        auditAppender
            .AppendAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var workItem = BuildWorkItem();

        await hook.OnActionAppliedAsync(workItem, DefaultActionId, "submitted", s_user, ct);

        var lost = Assert.Single(logger.Records, r =>
            r.Message.Contains("query-push-failed audit entry could not be persisted"));
        Assert.Equal(LogLevel.Error, lost.Level);
        Assert.Equal(workItem.Id, lost.Properties["WorkItemId"]);
    }
}
