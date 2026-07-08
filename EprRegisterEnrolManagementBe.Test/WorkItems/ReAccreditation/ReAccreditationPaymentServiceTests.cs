using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

/// <summary>
/// Unit tests for <see cref="ReAccreditationPaymentService"/>.
/// Persistence, notify client and audit appender are all substituted.
/// </summary>
public class ReAccreditationPaymentServiceTests
{
    private static readonly DateTimeOffset s_fixedNow = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static PaymentCompletedRequest ValidRequest(DateTime? paidAt = null) =>
        new()
        {
            AmountPence = 50000,
            Reference = "REF-001",
            PaidAt = paidAt ?? s_fixedNow.AddMinutes(-10).UtcDateTime,
            PaidByUserId = "op-user-1",
            PaidByEmail = "operator@example.com",
        };

    // RA-248: human-facing application reference stamped on the payload by the
    // core WorkItemService; expected in the ((reference)) Notify placeholder.
    private const string ApplicationReference = "RA-000123456";

    private static WorkItem BuildWorkItem(
        string stateId = "duly-made",
        string? applicationReference = ApplicationReference
    )
    {
        var payload = new BsonDocument
        {
            ["organisationName"] = "Acme Ltd",
            ["registrationNumber"] = "EX-001",
        };
        if (applicationReference is not null)
        {
            payload["applicationReference"] = applicationReference;
        }

        return new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            SubmittedBy = "test-client",
            Payload = payload,
        };
    }

    private sealed record Sut(
        ReAccreditationPaymentService Service,
        IWorkItemPersistence Persistence,
        INotifyClient NotifyClient,
        IWorkItemAuditAppender AuditAppender
    );

    private static Sut Build(WorkItem? workItem = null, DateTimeOffset? now = null)
    {
        var persistence = Substitute.For<IWorkItemPersistence>();
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        var time = new FakeTimeProvider(now ?? s_fixedNow);

        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-1"));
        auditAppender
            .AppendAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(),
                Arg.Any<System.Security.Claims.ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);

        if (workItem is not null)
        {
            persistence
                .GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(workItem);
        }

        var service = new ReAccreditationPaymentService(
            persistence,
            notifyClient,
            auditAppender,
            time,
            NullLogger<ReAccreditationPaymentService>.Instance
        );

        return new Sut(service, persistence, notifyClient, auditAppender);
    }

    // ── paidAt kind validation ──────────────────────────────────────────────

    [Fact]
    public async Task RecordPaymentAsync_rejects_paidAt_with_local_kind()
    {
        var workItem = BuildWorkItem();
        var sut = Build(workItem);
        var localPaidAt = DateTime.SpecifyKind(
            s_fixedNow.AddMinutes(-10).DateTime,
            DateTimeKind.Local
        );

        var result = await sut.Service.RecordPaymentAsync(
            workItem.Id,
            ValidRequest(localPaidAt),
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Contains("UTC", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordPaymentAsync_rejects_paidAt_with_unspecified_kind()
    {
        var workItem = BuildWorkItem();
        var sut = Build(workItem);
        var unspecifiedPaidAt = DateTime.SpecifyKind(
            s_fixedNow.AddMinutes(-10).DateTime,
            DateTimeKind.Unspecified
        );

        var result = await sut.Service.RecordPaymentAsync(
            workItem.Id,
            ValidRequest(unspecifiedPaidAt),
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
        Assert.Contains("UTC", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordPaymentAsync_accepts_paidAt_with_utc_kind()
    {
        var workItem = BuildWorkItem();
        var sut = Build(workItem);
        var utcPaidAt = DateTime.SpecifyKind(s_fixedNow.AddMinutes(-10).DateTime, DateTimeKind.Utc);

        var result = await sut.Service.RecordPaymentAsync(
            workItem.Id,
            ValidRequest(utcPaidAt),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsSuccess);
    }

    // ── sla-clock-started audit entry ───────────────────────────────────────

    [Fact]
    public async Task RecordPaymentAsync_sla_clock_started_audit_uses_actual_target_duration()
    {
        var workItem = BuildWorkItem();
        var sut = Build(workItem);

        var result = await sut.Service.RecordPaymentAsync(
            workItem.Id,
            ValidRequest(),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsSuccess);
        var clockEntry = result.WorkItem!.AuditLog.FirstOrDefault(e =>
            e.Action == "sla-clock-started"
        );
        Assert.NotNull(clockEntry);
        // Default TargetDuration is 84 days (12 weeks); the audit must
        // reflect the actual clock value, not a hardcoded literal.
        var expectedDays = result.WorkItem.SlaClock!.TargetDuration.TotalDays.ToString();
        Assert.Equal(expectedDays, clockEntry!.Details["targetDays"]);
        Assert.Equal("84", clockEntry.Details["targetDays"]);
    }

    // ── basic happy-path ────────────────────────────────────────────────────

    [Fact]
    public async Task RecordPaymentAsync_transitions_to_assessment_in_progress()
    {
        var workItem = BuildWorkItem();
        var sut = Build(workItem);

        var result = await sut.Service.RecordPaymentAsync(
            workItem.Id,
            ValidRequest(),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsSuccess);
        Assert.Equal("assessment-in-progress", result.WorkItem!.StateId);
    }

    [Fact]
    public async Task RecordPaymentAsync_stamps_sla_clock_with_paidAt()
    {
        var workItem = BuildWorkItem();
        var sut = Build(workItem);
        var paidAt = s_fixedNow.AddMinutes(-30).UtcDateTime;

        var result = await sut.Service.RecordPaymentAsync(
            workItem.Id,
            ValidRequest(paidAt),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkItem!.SlaClock);
        Assert.Equal(paidAt, result.WorkItem.SlaClock!.StartedAt);
    }

    [Fact]
    public async Task RecordPaymentAsync_returns_not_found_when_work_item_missing()
    {
        var sut = Build();

        var result = await sut.Service.RecordPaymentAsync(
            Guid.NewGuid(),
            ValidRequest(),
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.WorkItemNotFound, result.FailureCode);
    }

    [Fact]
    public async Task RecordPaymentAsync_returns_invalid_transition_when_not_duly_made()
    {
        var workItem = BuildWorkItem(stateId: "submitted");
        var sut = Build(workItem);

        var result = await sut.Service.RecordPaymentAsync(
            workItem.Id,
            ValidRequest(),
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidTransition, result.FailureCode);
    }

    [Fact]
    public async Task RecordPaymentAsync_returns_conflict_on_concurrency_exception()
    {
        var workItem = BuildWorkItem();
        var sut = Build(workItem);
        sut.Persistence.ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new WorkItemConcurrencyException(workItem.Id, 0));

        var result = await sut.Service.RecordPaymentAsync(
            workItem.Id,
            ValidRequest(),
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.ConcurrencyConflict, result.FailureCode);
    }

    // ── RA-248: application reference drives the ((reference)) placeholder ────

    [Fact]
    public async Task RecordPaymentAsync_uses_application_reference_for_personalisation_send_and_audit()
    {
        var workItem = BuildWorkItem();
        var sut = Build(workItem);

        Dictionary<string, string>? captured = null;
        Dictionary<string, string?>? auditDetails = null;
        sut.NotifyClient.SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-1"));
        sut.AuditAppender.AppendAsync(
                Arg.Any<Guid>(),
                "notification-sent",
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string?>>(d => auditDetails = d),
                Arg.Any<System.Security.Claims.ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);

        var result = await sut.Service.RecordPaymentAsync(
            workItem.Id,
            ValidRequest(),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsSuccess);
        Assert.NotNull(captured);
        Assert.Equal(ApplicationReference, captured!["reference"]);
        await sut.NotifyClient.Received(1)
            .SendEmailAsync(
                "AssessmentInProgress",
                "operator@example.com",
                Arg.Any<Dictionary<string, string>>(),
                ApplicationReference,
                cancellationToken: TestContext.Current.CancellationToken
            );
        Assert.NotNull(auditDetails);
        Assert.Equal(ApplicationReference, auditDetails!["reference"]);
    }

    [Fact]
    public async Task RecordPaymentAsync_falls_back_to_work_item_id_when_application_reference_absent()
    {
        // Legacy item predating RA-219: no applicationReference on the payload.
        var workItem = BuildWorkItem(applicationReference: null);
        var sut = Build(workItem);

        Dictionary<string, string>? captured = null;
        Dictionary<string, string?>? auditDetails = null;
        sut.NotifyClient.SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-1"));
        sut.AuditAppender.AppendAsync(
                Arg.Any<Guid>(),
                "notification-sent",
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string?>>(d => auditDetails = d),
                Arg.Any<System.Security.Claims.ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);

        var result = await sut.Service.RecordPaymentAsync(
            workItem.Id,
            ValidRequest(),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsSuccess);
        Assert.NotNull(captured);
        Assert.Equal(workItem.Id.ToString(), captured!["reference"]);
        await sut.NotifyClient.Received(1)
            .SendEmailAsync(
                "AssessmentInProgress",
                "operator@example.com",
                Arg.Any<Dictionary<string, string>>(),
                workItem.Id.ToString(),
                cancellationToken: TestContext.Current.CancellationToken
            );
        Assert.NotNull(auditDetails);
        Assert.Equal(workItem.Id.ToString(), auditDetails!["reference"]);
    }
}
