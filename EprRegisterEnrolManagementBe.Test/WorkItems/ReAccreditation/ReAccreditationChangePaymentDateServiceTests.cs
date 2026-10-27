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
/// RA-576 unit cover for <see cref="ReAccreditationChangePaymentDateService"/>:
/// the guards and refusals, that state and the SLA clock are left alone, the
/// audit entry, and the concurrency retry.
/// </summary>
public class ReAccreditationChangePaymentDateServiceTests
{
    private static readonly DateOnly s_originalDate = new(2026, 7, 15);
    private static readonly DateOnly s_newDate = new(2026, 7, 20);
    private static readonly DateTime s_slaStart = new(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
    private const string Reason = "Operator supplied the wrong date.";

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

    private static (ReAccreditationChangePaymentDateService Service, IWorkItemPersistence Persistence) Build(
        WorkItem? workItem,
        int conflictsBeforeSuccess = 0
    )
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        if (workItem is not null)
        {
            persistence
                .GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
                .Returns(workItem);
        }

        var conflicts = conflictsBeforeSuccess;
        persistence
            .ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
                conflicts-- > 0
                    ? Task.FromException(new WorkItemConcurrencyException(workItem!.Id, 0))
                    : Task.CompletedTask
            );

        var service = new ReAccreditationChangePaymentDateService(
            persistence,
            new WorkItemRegistry([new ReAccreditationType()]),
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 11, 10, 0, 0, TimeSpan.Zero)),
            NullLogger<ReAccreditationChangePaymentDateService>.Instance
        );
        return (service, persistence);
    }

    private static WorkItem BuildWorkItem(
        string stateId = "duly-made",
        string typeId = ReAccreditationType.Id,
        BsonDocument? payload = null
    ) =>
        new()
        {
            Id = Guid.NewGuid(),
            TypeId = typeId,
            StateId = stateId,
            SubmittedAt = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc),
            LastModifiedAt = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc),
            SubmittedBy = "test-client",
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType()),
            TemplateVersion = "v11",
            SlaClock = new WorkItemSlaClock { StartedAt = s_slaStart },
            Payload = payload
                ?? new BsonDocument
                {
                    ["organisationName"] = "Acme Ltd",
                    ["applicationReference"] = "RA-123456789",
                    ["paymentDate"] = "2026-07-15",
                    ["someUnmodelledKey"] = "must-survive",
                },
        };

    [Fact]
    public async Task Changing_the_payment_date_updates_the_payload_and_keeps_unmodelled_keys()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var (service, persistence) = Build(workItem);

        var result = await service.ChangePaymentDateAsync(workItem.Id, s_newDate, Reason, User(), ct);

        Assert.True(result.IsSuccess);
        var payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload!);
        Assert.Equal(s_newDate, payload.PaymentDate);
        Assert.Equal("must-survive", workItem.Payload!["someUnmodelledKey"].AsString);
        await persistence.Received(1).ReplaceAsync(workItem, ct);
    }

    /// <summary>AC05: neither the status nor the SLA clock ("Due on") moves.</summary>
    [Fact]
    public async Task State_and_sla_clock_are_left_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var (service, _) = Build(workItem);

        await service.ChangePaymentDateAsync(workItem.Id, s_newDate, Reason, User(), ct);

        Assert.Equal("duly-made", workItem.StateId);
        Assert.Equal(s_slaStart, workItem.SlaClock!.StartedAt);
    }

    /// <summary>AC06: one audit entry carries who, what changed, and why.</summary>
    [Fact]
    public async Task One_audit_entry_records_previous_date_new_date_and_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var (service, _) = Build(workItem);

        await service.ChangePaymentDateAsync(workItem.Id, s_newDate, Reason, User(), ct);

        var entry = Assert.Single(workItem.AuditLog);
        Assert.Equal("action-applied", entry.Action);
        Assert.Equal("alice-1", entry.CreatedBy);
        Assert.Equal("Alice Example", entry.CreatedByName);
        Assert.Equal("duly-made", entry.StateId);
        Assert.Equal("change-payment-date", entry.Details["actionId"]);
        Assert.Equal("Change payment date", entry.Details["actionDisplayName"]);
        Assert.Equal("duly-made", entry.Details["fromStateId"]);
        Assert.Equal("duly-made", entry.Details["toStateId"]);
        Assert.Equal("2026-07-15", entry.Details["previousPaymentDate"]);
        Assert.Equal("2026-07-20", entry.Details["paymentDate"]);
        Assert.Equal(Reason, entry.Details["reason"]);
    }

    /// <summary>AC01: the date can be changed more than once.</summary>
    [Fact]
    public async Task A_second_change_records_the_first_result_as_the_previous_date()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var (service, _) = Build(workItem);

        await service.ChangePaymentDateAsync(workItem.Id, s_newDate, Reason, User(), ct);
        await service.ChangePaymentDateAsync(workItem.Id, s_originalDate, "Changed back.", User(), ct);

        Assert.Equal(2, workItem.AuditLog.Count);
        Assert.Equal("2026-07-20", workItem.AuditLog[1].Details["previousPaymentDate"]);
        Assert.Equal("2026-07-15", workItem.AuditLog[1].Details["paymentDate"]);
    }

    [Theory]
    [InlineData("queried")]
    [InlineData("updated")]
    [InlineData("assessment-in-progress")]
    [InlineData("awaiting-decision")]
    [InlineData("approved-pending-contact")]
    [InlineData("rejected-pending-contact")]
    public async Task Non_terminal_states_remain_editable(string stateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(stateId);
        var (service, _) = Build(workItem);

        var result = await service.ChangePaymentDateAsync(workItem.Id, s_newDate, Reason, User(), ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(stateId, workItem.StateId);
    }

    /// <summary>AC02: closed applications are refused and nothing is written.</summary>
    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    public async Task Terminal_states_are_refused(string stateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(stateId);
        var (service, persistence) = Build(workItem);

        var result = await service.ChangePaymentDateAsync(workItem.Id, s_newDate, Reason, User(), ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.TerminalState, result.FailureCode);
        await persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
        Assert.Empty(workItem.AuditLog);
    }

    [Fact]
    public async Task An_item_with_no_payment_date_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(payload: new BsonDocument { ["organisationName"] = "Acme Ltd" });
        var (service, persistence) = Build(workItem);

        var result = await service.ChangePaymentDateAsync(workItem.Id, s_newDate, Reason, User(), ct);

        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        await persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_work_item_is_reported_as_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, _) = Build(null);

        var result = await service.ChangePaymentDateAsync(Guid.NewGuid(), s_newDate, Reason, User(), ct);

        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task Another_work_item_type_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(typeId: "some-other-type");
        var (service, _) = Build(workItem);

        var result = await service.ChangePaymentDateAsync(workItem.Id, s_newDate, Reason, User(), ct);

        Assert.Equal(WorkItemActionFailureCode.UnknownAction, result.FailureCode);
    }

    [Fact]
    public async Task A_request_without_a_user_id_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var (service, persistence) = Build(workItem);

        var result = await service.ChangePaymentDateAsync(workItem.Id, s_newDate, Reason, User(null), ct);

        Assert.Equal(WorkItemActionFailureCode.MissingActorIdentity, result.FailureCode);
        await persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_concurrency_conflict_is_retried_without_duplicating_the_audit_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var (service, persistence) = Build(workItem, conflictsBeforeSuccess: 1);
        // Mimic a real reload: each attempt starts from the stored document.
        var stored = workItem.AuditLog.Count;
        persistence
            .GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                while (workItem.AuditLog.Count > stored)
                {
                    workItem.AuditLog.RemoveAt(workItem.AuditLog.Count - 1);
                }
                return workItem;
            });

        var result = await service.ChangePaymentDateAsync(workItem.Id, s_newDate, Reason, User(), ct);

        Assert.True(result.IsSuccess);
        Assert.Single(workItem.AuditLog);
        await persistence.Received(2).ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Repeated_concurrency_conflicts_end_in_a_conflict_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var (service, persistence) = Build(workItem, conflictsBeforeSuccess: int.MaxValue);

        var result = await service.ChangePaymentDateAsync(workItem.Id, s_newDate, Reason, User(), ct);

        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.FailureCode);
        await persistence.Received(3).ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }
}
