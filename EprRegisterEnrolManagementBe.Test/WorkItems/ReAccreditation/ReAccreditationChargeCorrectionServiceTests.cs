using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// RA-766 (temporary) unit cover for <see cref="ReAccreditationChargeCorrectionService"/>:
/// the guards and refusals, that state and the SLA clock are left alone, the audit
/// entry, and the concurrency retry.
/// </summary>
public class ReAccreditationChargeCorrectionServiceTests
{
    private static readonly DateTime s_slaStart = new(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);

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

    private static (ReAccreditationChargeCorrectionService Service, IWorkItemPersistence Persistence) Build(
        WorkItem? workItem,
        int conflictsBeforeSuccess = 0,
        bool registerType = true
    )
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        if (workItem is not null)
        {
            persistence.GetByIdAsync(workItem.Id, Arg.Any<CancellationToken>()).Returns(workItem);
        }

        var conflicts = conflictsBeforeSuccess;
        persistence
            .ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
                conflicts-- > 0
                    ? Task.FromException(new WorkItemConcurrencyException(workItem!.Id, 0))
                    : Task.CompletedTask
            );

        var service = new ReAccreditationChargeCorrectionService(
            persistence,
            new WorkItemRegistry(registerType ? [new ReAccreditationType()] : []),
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 11, 10, 0, 0, TimeSpan.Zero)),
            NullLogger<ReAccreditationChargeCorrectionService>.Instance
        );
        return (service, persistence);
    }

    private static WorkItem BuildWorkItem(
        string stateId = "queried",
        string typeId = ReAccreditationType.Id,
        BsonDocument? payload = null,
        bool withSnapshot = true
    ) =>
        new()
        {
            Id = Guid.NewGuid(),
            TypeId = typeId,
            StateId = stateId,
            SubmittedAt = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc),
            LastModifiedAt = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc),
            SubmittedBy = "test-client",
            TemplateSnapshot = withSnapshot
                ? WorkItemTemplateSnapshot.Capture(new ReAccreditationType())
                : null,
            TemplateVersion = "v11",
            SlaClock = new WorkItemSlaClock { StartedAt = s_slaStart },
            Payload = payload
                ?? new BsonDocument
                {
                    ["organisationName"] = "Acme Ltd",
                    ["chargeAmountPence"] = 218400,
                    ["someUnmodelledKey"] = "must-survive",
                },
        };

    // Real persistence hands back a fresh document on every read, so a retry after a
    // concurrency conflict never sees the previous attempt's in-memory edit.
    private static void ReadFreshCopiesOf(IWorkItemPersistence persistence, WorkItem original) =>
        persistence
            .GetByIdAsync(original.Id, Arg.Any<CancellationToken>())
            .Returns(_ => new WorkItem
            {
                Id = original.Id,
                TypeId = original.TypeId,
                StateId = original.StateId,
                SubmittedAt = original.SubmittedAt,
                LastModifiedAt = original.LastModifiedAt,
                SubmittedBy = original.SubmittedBy,
                TemplateSnapshot = original.TemplateSnapshot,
                TemplateVersion = original.TemplateVersion,
                SlaClock = original.SlaClock,
                Payload = original.Payload!.DeepClone().AsBsonDocument,
            });

    [Fact]
    public async Task A_different_charge_is_saved_and_every_other_payload_key_is_kept()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var (service, persistence) = Build(workItem);

        var result = await service.CorrectChargeAsync(workItem.Id, 327600, User(), ct);

        Assert.Null(result.Failure);
        Assert.Equal(ChargeCorrectionResult.Updated, result.Outcome);
        Assert.Equal(327600, result.ChargeAmountPence);
        Assert.Equal(327600, workItem.Payload!["chargeAmountPence"].AsInt32);
        Assert.Equal("must-survive", workItem.Payload["someUnmodelledKey"].AsString);
        Assert.Equal("Acme Ltd", workItem.Payload["organisationName"].AsString);
        await persistence.Received(1).ReplaceAsync(workItem, ct);
    }

    [Fact]
    public async Task State_and_sla_clock_are_left_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem("duly-made");
        var (service, _) = Build(workItem);

        await service.CorrectChargeAsync(workItem.Id, 327600, User(), ct);

        Assert.Equal("duly-made", workItem.StateId);
        Assert.Equal(s_slaStart, workItem.SlaClock!.StartedAt);
    }

    [Fact]
    public async Task One_audit_entry_records_who_the_old_and_new_charge_and_the_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(
            payload: new BsonDocument { ["chargeAmountPence"] = 218400, ["paymentDate"] = "2026-07-15" }
        );
        var (service, _) = Build(workItem);

        await service.CorrectChargeAsync(workItem.Id, 327600, User(), ct);

        var entry = Assert.Single(workItem.AuditLog);
        Assert.Equal("charge-corrected", entry.Action);
        Assert.Equal("Charge corrected", entry.ActionDisplayName);
        Assert.Equal("alice-1", entry.CreatedBy);
        Assert.Equal("Alice Example", entry.CreatedByName);
        Assert.Equal("queried", entry.StateId);
        Assert.Equal("218400", entry.Details["previousChargeAmountPence"]);
        Assert.Equal("327600", entry.Details["chargeAmountPence"]);
        Assert.Equal("true", entry.Details["dulyMade"]);
        Assert.Equal("RA-766", entry.Details["reason"]);
    }

    [Fact]
    public async Task A_missing_charge_is_recorded_with_no_previous_value_and_not_duly_made()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem(payload: new BsonDocument());
        var (service, _) = Build(workItem);

        var result = await service.CorrectChargeAsync(workItem.Id, 54600, User(), ct);

        Assert.False(result.DulyMade);
        var entry = Assert.Single(workItem.AuditLog);
        Assert.Null(entry.Details["previousChargeAmountPence"]);
        Assert.Equal("false", entry.Details["dulyMade"]);
    }

    [Fact]
    public async Task An_equal_charge_is_unchanged_and_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var (service, persistence) = Build(workItem);

        var result = await service.CorrectChargeAsync(workItem.Id, 218400, User(), ct);

        Assert.Equal(ChargeCorrectionResult.Unchanged, result.Outcome);
        Assert.Empty(workItem.AuditLog);
        await persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_second_identical_call_is_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var (service, _) = Build(workItem);

        await service.CorrectChargeAsync(workItem.Id, 327600, User(), ct);
        var second = await service.CorrectChargeAsync(workItem.Id, 327600, User(), ct);

        Assert.Equal(ChargeCorrectionResult.Unchanged, second.Outcome);
        Assert.Single(workItem.AuditLog);
    }

    [Fact]
    public async Task A_request_with_no_end_user_is_refused_before_anything_is_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var (service, persistence) = Build(workItem);

        var result = await service.CorrectChargeAsync(workItem.Id, 327600, User(userId: null), ct);

        Assert.Equal(WorkItemActionFailureCode.MissingActorIdentity, result.Failure!.FailureCode);
        await persistence.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_work_item_is_not_found()
    {
        var (service, _) = Build(null);

        var result = await service.CorrectChargeAsync(
            Guid.NewGuid(), 327600, User(), TestContext.Current.CancellationToken);

        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.Failure!.FailureCode);
    }

    [Fact]
    public async Task A_work_item_of_another_type_is_refused()
    {
        var workItem = BuildWorkItem(typeId: "some-other-type");
        var (service, persistence) = Build(workItem);

        var result = await service.CorrectChargeAsync(
            workItem.Id, 327600, User(), TestContext.Current.CancellationToken);

        Assert.Equal(WorkItemActionFailureCode.UnknownAction, result.Failure!.FailureCode);
        await persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_work_item_with_no_resolvable_template_is_refused()
    {
        // No snapshot on the item and the type is not registered: terminality is unknown,
        // which must not be read as "open".
        var workItem = BuildWorkItem(withSnapshot: false);
        var (service, persistence) = Build(workItem, registerType: false);

        var result = await service.CorrectChargeAsync(
            workItem.Id, 327600, User(), TestContext.Current.CancellationToken);

        Assert.Equal(WorkItemActionFailureCode.UnknownAction, result.Failure!.FailureCode);
        await persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    [InlineData("approved-pending-contact")]
    [InlineData("rejected-pending-contact")]
    public async Task A_closed_work_item_is_refused_and_unchanged(string stateId)
    {
        var workItem = BuildWorkItem(stateId);
        var (service, persistence) = Build(workItem);

        var result = await service.CorrectChargeAsync(
            workItem.Id, 327600, User(), TestContext.Current.CancellationToken);

        Assert.Equal(WorkItemActionFailureCode.TerminalState, result.Failure!.FailureCode);
        Assert.Equal(218400, workItem.Payload!["chargeAmountPence"].AsInt32);
        Assert.Empty(workItem.AuditLog);
        await persistence.DidNotReceive().ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("submitted")]
    [InlineData("duly-made")]
    [InlineData("queried")]
    [InlineData("updated")]
    [InlineData("assessment-in-progress")]
    [InlineData("awaiting-decision")]
    public async Task Every_open_state_is_correctable(string stateId)
    {
        var workItem = BuildWorkItem(stateId);
        var (service, _) = Build(workItem);

        var result = await service.CorrectChargeAsync(
            workItem.Id, 327600, User(), TestContext.Current.CancellationToken);

        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task A_concurrency_conflict_is_retried_against_a_fresh_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var (service, persistence) = Build(workItem, conflictsBeforeSuccess: 1);
        ReadFreshCopiesOf(persistence, workItem);

        var result = await service.CorrectChargeAsync(workItem.Id, 327600, User(), ct);

        Assert.Null(result.Failure);
        Assert.Equal(ChargeCorrectionResult.Updated, result.Outcome);
        await persistence.Received(2).ReplaceAsync(Arg.Any<WorkItem>(), ct);
        await persistence.Received(2).GetByIdAsync(workItem.Id, ct);
    }

    [Fact]
    public async Task Repeated_concurrency_conflicts_fail_with_a_conflict_after_three_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        var workItem = BuildWorkItem();
        var (service, persistence) = Build(workItem, conflictsBeforeSuccess: 10);
        ReadFreshCopiesOf(persistence, workItem);

        var result = await service.CorrectChargeAsync(workItem.Id, 327600, User(), ct);

        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.Failure!.FailureCode);
        await persistence.Received(3).ReplaceAsync(Arg.Any<WorkItem>(), ct);
    }

    [Theory]
    [InlineData("approved", true)]
    [InlineData("rejected", true)]
    [InlineData("withdrawn", true)]
    [InlineData("approved-pending-contact", true)]
    [InlineData("rejected-pending-contact", true)]
    [InlineData("APPROVED-PENDING-CONTACT", true)]
    [InlineData("queried", false)]
    [InlineData("duly-made", false)]
    public void IsClosed_is_the_terminal_states_plus_the_two_pending_contact_states(
        string stateId, bool expected)
    {
        var template = new ReAccreditationType();

        Assert.Equal(expected, ReAccreditationChargeCorrectionService.IsClosed(template, stateId));
    }
}
