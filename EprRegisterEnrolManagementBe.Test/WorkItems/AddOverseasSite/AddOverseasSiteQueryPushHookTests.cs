using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.AddOverseasSite;

/// <summary>
/// RA-698 (MBE-3): the hook that pushes an add-ORS query to the operator
/// backend. It verifies the stamped query was raised inside the transition's
/// window, records one audit entry per outcome and never throws.
/// </summary>
public sealed class AddOverseasSiteQueryPushHookTests
{
    private static readonly ClaimsPrincipal s_user = new(new ClaimsIdentity([new Claim("user:id", "alice-1")], "test"));

    private const string DefaultActionId = "query-during-duly-making";

    private static WorkItem BuildWorkItem(
        string typeId = AddOverseasSiteType.Id,
        string actionId = DefaultActionId,
        BsonDocument? currentQuery = null,
        bool omitCurrentQuery = false
    )
    {
        var payload = new BsonDocument { ["organisationName"] = "Acme Ltd" };
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

    private static (
        AddOverseasSiteQueryPushHook Hook,
        IOperatorBackendPushAdapter Adapter,
        IWorkItemAuditAppender Audit
    ) BuildSut(OperatorBackendPushResult? result = null, bool auditPersists = true)
    {
        var adapter = Substitute.For<IOperatorBackendPushAdapter>();
        adapter
            .PushChangeRequestQueryRaisedAsync(
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(result ?? OperatorBackendPushResult.Success());
        var audit = Substitute.For<IWorkItemAuditAppender>();
        audit
            .AppendAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(auditPersists);
        return (
            new AddOverseasSiteQueryPushHook(adapter, audit, NullLogger<AddOverseasSiteQueryPushHook>.Instance),
            adapter,
            audit
        );
    }

    private static Task ApplyAsync(
        AddOverseasSiteQueryPushHook hook,
        WorkItem workItem,
        string actionId = DefaultActionId
    ) =>
        hook.OnActionAppliedAsync(
            workItem,
            actionId,
            FreshQueryTestData.FromStateFor(actionId),
            s_user,
            TestContext.Current.CancellationToken
        );

    [Theory]
    [InlineData("query-during-duly-making")]
    [InlineData("query-during-duly-made")]
    [InlineData("query-during-assessment")]
    [InlineData("query-during-decision")]
    public async Task Each_query_action_pushes_the_note_and_the_raised_time(string actionId)
    {
        var (hook, adapter, audit) = BuildSut();
        var workItem = BuildWorkItem(actionId: actionId);

        await ApplyAsync(hook, workItem, actionId);

        await adapter
            .Received(1)
            .PushChangeRequestQueryRaisedAsync(
                workItem.Id,
                Arg.Any<Guid>(),
                FreshQueryTestData.Reason,
                FreshQueryTestData.RaisedAt,
                Arg.Any<CancellationToken>()
            );
        await audit
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "query-push-sent",
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(),
                s_user,
                Arg.Any<CancellationToken>()
            );
    }

    [Theory]
    [InlineData("withdraw", AddOverseasSiteType.Id)]
    [InlineData(DefaultActionId, "some-other-type")]
    public async Task Other_actions_and_other_types_push_nothing(string actionId, string typeId)
    {
        var (hook, adapter, audit) = BuildSut();
        var workItem = BuildWorkItem(typeId: typeId);

        await hook.OnActionAppliedAsync(workItem, actionId, "submitted", s_user, TestContext.Current.CancellationToken);

        Assert.Empty(adapter.ReceivedCalls());
        Assert.Empty(audit.ReceivedCalls());
    }

    [Fact]
    public async Task A_skipped_push_is_recorded_as_skipped_with_its_reason()
    {
        var (hook, _, audit) = BuildSut(OperatorBackendPushResult.Skipped("OperatorBackendApi:Enabled is false."));
        var workItem = BuildWorkItem();

        await ApplyAsync(hook, workItem);

        await audit
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "query-push-skipped",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d => d["reason"] == "OperatorBackendApi:Enabled is false."),
                s_user,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task A_failed_push_is_recorded_as_failed_with_the_error()
    {
        var (hook, _, audit) = BuildSut(OperatorBackendPushResult.Failure("Operator backend returned 500."));
        var workItem = BuildWorkItem();

        await ApplyAsync(hook, workItem);

        await audit
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "query-push-failed",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d => d["errorMessage"] == "Operator backend returned 500."),
                s_user,
                Arg.Any<CancellationToken>()
            );
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_audit_entry_that_cannot_be_persisted_does_not_throw(bool failedPush)
    {
        var (hook, _, audit) = BuildSut(
            failedPush ? OperatorBackendPushResult.Failure("boom") : OperatorBackendPushResult.Success(),
            auditPersists: false
        );

        await ApplyAsync(hook, BuildWorkItem());

        Assert.Single(audit.ReceivedCalls());
    }

    [Fact]
    public async Task A_throwing_adapter_is_swallowed()
    {
        var (hook, adapter, audit) = BuildSut();
        adapter
            .PushChangeRequestQueryRaisedAsync(
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>()
            )
            .Returns<OperatorBackendPushResult>(_ => throw new InvalidOperationException("boom"));

        await ApplyAsync(hook, BuildWorkItem());

        Assert.Empty(audit.ReceivedCalls());
    }

    public static TheoryData<string, string> Unverifiable =>
        new()
        {
            { "missing", "payload.currentQuery is missing" },
            { "not-a-document", "payload.currentQuery is missing" },
            { "no-raisedAt", "raisedAt is missing" },
            { "no-reason", "reason is missing" },
            { "no-transition", "no action-applied entry" },
            { "wrong-transition", "no action-applied entry" },
            { "stale", "earlier query" },
            { "late", "after the" },
        };

    private static WorkItem BuildUnverifiable(string scenario)
    {
        var item = scenario switch
        {
            "missing" => BuildWorkItem(omitCurrentQuery: true),
            "no-raisedAt" => BuildWorkItem(currentQuery: new BsonDocument { ["reason"] = "Why" }),
            "no-reason" => BuildWorkItem(currentQuery: new BsonDocument { ["raisedAt"] = FreshQueryTestData.RaisedAt }),
            "stale" => BuildWorkItem(
                currentQuery: FreshQueryTestData.CurrentQuery(FreshQueryTestData.StaleRaisedAt)
            ),
            "late" => BuildWorkItem(
                currentQuery: FreshQueryTestData.CurrentQuery(FreshQueryTestData.TransitionAt.AddMinutes(1))
            ),
            _ => BuildWorkItem(),
        };

        switch (scenario)
        {
            case "not-a-document":
                item.Payload["currentQuery"] = "text";
                break;
            case "no-transition":
                item.AuditLog.Clear();
                break;
            case "wrong-transition":
                item.AuditLog.Clear();
                item.AuditLog.Add(
                    FreshQueryTestData.ActionApplied(
                        "query-during-decision",
                        "awaiting-decision",
                        "queried",
                        FreshQueryTestData.TransitionAt
                    )
                );
                break;
        }

        return item;
    }

    [Theory]
    [MemberData(nameof(Unverifiable))]
    public async Task A_query_that_cannot_be_verified_is_not_pushed_and_is_recorded_as_failed(
        string scenario,
        string expectedReasonFragment
    )
    {
        var (hook, adapter, audit) = BuildSut();
        var workItem = BuildUnverifiable(scenario);

        await ApplyAsync(hook, workItem);

        Assert.Empty(adapter.ReceivedCalls());
        await audit
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "query-push-failed",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d => d["reason"]!.Contains(expectedReasonFragment)),
                s_user,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Submission_and_assignment_events_are_ignored()
    {
        var (hook, adapter, _) = BuildSut();
        var workItem = BuildWorkItem();
        var ct = TestContext.Current.CancellationToken;

        await hook.OnSubmittedAsync(workItem, s_user, ct);
        await hook.OnAssignmentChangedAsync(workItem, default!, s_user, ct);

        Assert.Empty(adapter.ReceivedCalls());
    }

    [Fact]
    public async Task The_null_adapter_reports_a_change_request_query_push_as_skipped()
    {
        var result = await new NullOperatorBackendPushAdapter().PushChangeRequestQueryRaisedAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "note",
            DateTime.UtcNow,
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsSkipped);
    }
}
