using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
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

    private static WorkItem BuildWorkItem(
        string typeId = ReAccreditationType.Id,
        string? reason = "Tonnage does not reconcile",
        string[]? sections = null)
    {
        var payload = new BsonDocument
        {
            ["organisationName"] = "Acme Ltd",
        };
        if (reason is not null)
        {
            payload["currentQuery"] = new BsonDocument
            {
                ["reason"] = reason,
                ["sections"] = new BsonArray(sections ?? ["business-plan", "prn-tonnage"]),
            };
        }

        return new WorkItem { TypeId = typeId, StateId = "queried", Payload = payload };
    }

    private static (ReAccreditationQueryPushHook Hook, IOperatorBackendPushAdapter Adapter, IWorkItemAuditAppender AuditAppender)
        BuildSut()
    {
        var adapter = Substitute.For<IOperatorBackendPushAdapter>();
        adapter
            .PushQueryRaisedAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(OperatorBackendPushResult.Success());
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        auditAppender
            .AppendAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var hook = new ReAccreditationQueryPushHook(
            adapter, auditAppender, NullLogger<ReAccreditationQueryPushHook>.Instance);
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
        var workItem = BuildWorkItem();

        await hook.OnActionAppliedAsync(workItem, actionId, "submitted", s_user, ct);

        var expectedSections = new[] { "business-plan", "prn-tonnage" };
        await adapter.Received(1).PushQueryRaisedAsync(
            workItem.Id,
            "Tonnage does not reconcile",
            Arg.Is<IReadOnlyList<string>>(s => s.SequenceEqual(expectedSections)),
            ct);
    }

    [Theory]
    [InlineData("submit-for-decision")]
    [InlineData("payment-received")]
    [InlineData("withdraw")]
    [InlineData("approve")]
    public async Task OnActionAppliedAsync_ignores_non_query_actions(string actionId)
    {
        var ct = TestContext.Current.CancellationToken;
        var (hook, adapter, _) = BuildSut();
        var workItem = BuildWorkItem();

        await hook.OnActionAppliedAsync(workItem, actionId, "submitted", s_user, ct);

        await adapter.DidNotReceiveWithAnyArgs()
            .PushQueryRaisedAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task OnActionAppliedAsync_ignores_a_work_item_of_a_different_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var (hook, adapter, _) = BuildSut();
        var workItem = BuildWorkItem(typeId: "some-other-type");

        await hook.OnActionAppliedAsync(workItem, "query-during-duly-making", "submitted", s_user, ct);

        await adapter.DidNotReceiveWithAnyArgs()
            .PushQueryRaisedAsync(default, default!, default!, default);
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
    public async Task OnActionAppliedAsync_records_a_skipped_audit_entry_when_the_push_is_disabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var (hook, adapter, auditAppender) = BuildSut();
        adapter
            .PushQueryRaisedAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
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
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(OperatorBackendPushResult.Failure("connection refused"));
        var workItem = BuildWorkItem();

        await hook.OnActionAppliedAsync(workItem, "query-during-duly-making", "submitted", s_user, ct);

        await auditAppender.Received(1).AppendAsync(
            workItem.Id, "query-push-failed", Arg.Any<string>(),
            Arg.Is<Dictionary<string, string?>>(d => d["errorMessage"] == "connection refused"),
            s_user, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_never_throws_when_the_adapter_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var (hook, adapter, _) = BuildSut();
        adapter
            .PushQueryRaisedAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<OperatorBackendPushResult>(_ => throw new InvalidOperationException("boom"));
        var workItem = BuildWorkItem();

        // Should not throw.
        await hook.OnActionAppliedAsync(workItem, "query-during-duly-making", "submitted", s_user, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_never_throws_on_an_unparseable_payload()
    {
        var ct = TestContext.Current.CancellationToken;
        var (hook, _, _) = BuildSut();
        var workItem = new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "queried",
            // currentQuery modelled as a scalar instead of a document —
            // BsonSerializer.Deserialize<ReAccreditationPayload> throws.
            Payload = new BsonDocument { ["currentQuery"] = "not-a-document" },
        };

        // Should not throw.
        await hook.OnActionAppliedAsync(workItem, "query-during-duly-making", "submitted", s_user, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_pushes_an_empty_note_when_there_is_no_current_query()
    {
        var ct = TestContext.Current.CancellationToken;
        var (hook, adapter, _) = BuildSut();
        var workItem = BuildWorkItem(reason: null);

        await hook.OnActionAppliedAsync(workItem, "query-during-duly-making", "submitted", s_user, ct);

        await adapter.Received(1).PushQueryRaisedAsync(
            workItem.Id, string.Empty, Arg.Is<IReadOnlyList<string>>(s => s.Count == 0), ct);
    }
}
