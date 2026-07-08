using System.Security.Claims;
using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

public class ReAccreditationDulyMadeHookTests
{
    private static readonly ClaimsPrincipal s_user = new(
        new ClaimsIdentity(
            [new Claim("user:id", "user-1"), new Claim("user:name", "Alice")],
            "test"
        )
    );

    // RA-248: human-facing application reference stamped on the payload by the
    // core WorkItemService; expected in the ((reference)) Notify placeholder.
    private const string ApplicationReference = "RA-000123456";

    private static WorkItem BuildWorkItem(
        string stateId = "submitted",
        string? operatorEmail = "op@example.com",
        string typeId = ReAccreditationType.Id,
        string? applicationReference = ApplicationReference
    )
    {
        var payload = new BsonDocument
        {
            ["organisationName"] = "Acme Ltd",
            ["registrationNumber"] = "EX-001",
        };
        if (operatorEmail is not null)
        {
            payload["operatorEmail"] = operatorEmail;
        }
        if (applicationReference is not null)
        {
            payload["applicationReference"] = applicationReference;
        }

        return new WorkItem
        {
            TypeId = typeId,
            StateId = stateId,
            Payload = payload,
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType()),
            TemplateVersion = "v5",
        };
    }

    private static ReAccreditationDulyMadeHook BuildSut(
        IWorkItemPersistence persistence,
        INotifyClient notifyClient,
        IWorkItemAuditAppender auditAppender,
        TimeProvider? timeProvider = null
    ) =>
        new(
            persistence,
            notifyClient,
            auditAppender,
            timeProvider ?? TimeProvider.System,
            NullLogger<ReAccreditationDulyMadeHook>.Instance
        );

    [Fact]
    public async Task OnAllTasksCompletedAsync_transitions_submitted_to_duly_made_and_appends_audit_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistence = Substitute.For<IWorkItemPersistence>();
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-id"));

        var workItem = BuildWorkItem();
        var sut = BuildSut(persistence, notifyClient, auditAppender);

        await sut.OnAllTasksCompletedAsync(workItem, "submitted", s_user, ct);

        Assert.Equal("duly-made", workItem.StateId);
        Assert.Contains(
            workItem.AuditLog,
            e =>
                e.Action == "action-applied"
                && e.Details["actionId"] == "duly-make"
                && e.Details["fromStateId"] == "submitted"
                && e.Details["toStateId"] == "duly-made"
        );
        await persistence.Received(1).ReplaceAsync(workItem, ct);
    }

    [Fact]
    public async Task OnAllTasksCompletedAsync_sends_DulyMade_email_after_transition()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistence = Substitute.For<IWorkItemPersistence>();
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-id"));

        var workItem = BuildWorkItem();
        var sut = BuildSut(persistence, notifyClient, auditAppender);

        await sut.OnAllTasksCompletedAsync(workItem, "submitted", s_user, ct);

        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "DulyMade",
                "op@example.com",
                Arg.Any<Dictionary<string, string>>(),
                ApplicationReference,
                cancellationToken: ct
            );
    }

    [Fact]
    public async Task OnAllTasksCompletedAsync_records_notification_sent_audit_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistence = Substitute.For<IWorkItemPersistence>();
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-id"));

        var workItem = BuildWorkItem();
        var sut = BuildSut(persistence, notifyClient, auditAppender);

        await sut.OnAllTasksCompletedAsync(workItem, "submitted", s_user, ct);

        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-sent",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["templateKey"] == "DulyMade" && d["providerMessageId"] == "msg-id"
                ),
                s_user,
                ct
            );
    }

    [Fact]
    public async Task OnAllTasksCompletedAsync_records_notification_failed_audit_entry_and_does_not_throw()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistence = Substitute.For<IWorkItemPersistence>();
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Failure("timeout"));

        var workItem = BuildWorkItem();
        var sut = BuildSut(persistence, notifyClient, auditAppender);

        await sut.OnAllTasksCompletedAsync(workItem, "submitted", s_user, ct);

        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-failed",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["templateKey"] == "DulyMade" && d["errorMessage"] == "timeout"
                ),
                s_user,
                ct
            );
    }

    [Fact]
    public async Task OnAllTasksCompletedAsync_skips_notification_and_records_skipped_when_no_operator_email()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistence = Substitute.For<IWorkItemPersistence>();
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();

        var workItem = BuildWorkItem(operatorEmail: null);
        var sut = BuildSut(persistence, notifyClient, auditAppender);

        await sut.OnAllTasksCompletedAsync(workItem, "submitted", s_user, ct);

        await notifyClient
            .DidNotReceiveWithAnyArgs()
            .SendEmailAsync(default!, default!, default!, default!, default!, ct);
        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-skipped",
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(),
                s_user,
                ct
            );
    }

    [Fact]
    public async Task OnAllTasksCompletedAsync_does_nothing_for_non_re_accreditation_types()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistence = Substitute.For<IWorkItemPersistence>();
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();

        var workItem = BuildWorkItem(typeId: "some-other-type");
        var sut = BuildSut(persistence, notifyClient, auditAppender);

        await sut.OnAllTasksCompletedAsync(workItem, "submitted", s_user, ct);

        Assert.Equal("submitted", workItem.StateId);
        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }

    [Fact]
    public async Task OnAllTasksCompletedAsync_does_nothing_for_non_submitted_states()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistence = Substitute.For<IWorkItemPersistence>();
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();

        var workItem = BuildWorkItem(stateId: "duly-made");
        var sut = BuildSut(persistence, notifyClient, auditAppender);

        await sut.OnAllTasksCompletedAsync(workItem, "duly-made", s_user, ct);

        Assert.Equal("duly-made", workItem.StateId);
        await persistence.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
    }

    [Fact]
    public async Task OnAllTasksCompletedAsync_throws_when_persist_fails_so_task_completion_returns_500()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistence = Substitute.For<IWorkItemPersistence>();
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        persistence
            .ReplaceAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("DB error")));

        var workItem = BuildWorkItem();
        var sut = BuildSut(persistence, notifyClient, auditAppender);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.OnAllTasksCompletedAsync(workItem, "submitted", s_user, ct)
        );

        await notifyClient
            .DidNotReceiveWithAnyArgs()
            .SendEmailAsync(default!, default!, default!, default!, default!, ct);
    }

    [Fact]
    public async Task OnAllTasksCompletedAsync_starts_sla_clock_on_successful_transition()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistence = Substitute.For<IWorkItemPersistence>();
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-id"));

        var fakeTime = new FakeTimeProvider();
        var frozenNow = new DateTimeOffset(2025, 6, 1, 9, 0, 0, TimeSpan.Zero);
        fakeTime.SetUtcNow(frozenNow);

        var workItem = BuildWorkItem();
        var sut = BuildSut(persistence, notifyClient, auditAppender, fakeTime);

        await sut.OnAllTasksCompletedAsync(workItem, "submitted", s_user, ct);

        Assert.NotNull(workItem.SlaClock);
        Assert.Equal(frozenNow.UtcDateTime, workItem.SlaClock!.StartedAt);
    }

    [Fact]
    public async Task OnAllTasksCompletedAsync_appends_sla_clock_started_audit_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistence = Substitute.For<IWorkItemPersistence>();
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-id"));

        var workItem = BuildWorkItem();
        var sut = BuildSut(persistence, notifyClient, auditAppender);

        await sut.OnAllTasksCompletedAsync(workItem, "submitted", s_user, ct);

        Assert.Contains(
            workItem.AuditLog,
            e =>
                e.Action == "sla-clock-started"
                && e.Details.ContainsKey("startedAt")
                && e.Details.ContainsKey("targetDays")
        );
    }

    [Fact]
    public async Task personalisation_includes_organisation_name_registration_number_and_reference()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistence = Substitute.For<IWorkItemPersistence>();
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("id"));

        var workItem = BuildWorkItem();
        var sut = BuildSut(persistence, notifyClient, auditAppender);

        await sut.OnAllTasksCompletedAsync(workItem, "submitted", s_user, ct);

        Assert.NotNull(captured);
        Assert.Equal("Acme Ltd", captured!["organisation_name"]);
        Assert.Equal("EX-001", captured["registration_number"]);
        Assert.Equal(ApplicationReference, captured["reference"]);
    }

    // ─────── RA-248: application reference drives the ((reference)) placeholder ───────

    [Fact]
    public async Task DulyMade_uses_application_reference_for_personalisation_send_and_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistence = Substitute.For<IWorkItemPersistence>();
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        Dictionary<string, string?>? auditDetails = null;
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-id"));
        auditAppender
            .AppendAsync(
                Arg.Any<Guid>(),
                "notification-sent",
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string?>>(d => auditDetails = d),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);

        var workItem = BuildWorkItem();
        var sut = BuildSut(persistence, notifyClient, auditAppender);

        await sut.OnAllTasksCompletedAsync(workItem, "submitted", s_user, ct);

        Assert.NotNull(captured);
        Assert.Equal(ApplicationReference, captured!["reference"]);
        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "DulyMade",
                "op@example.com",
                Arg.Any<Dictionary<string, string>>(),
                ApplicationReference,
                cancellationToken: ct
            );
        Assert.NotNull(auditDetails);
        Assert.Equal(ApplicationReference, auditDetails!["reference"]);
    }

    [Fact]
    public async Task DulyMade_falls_back_to_work_item_id_when_application_reference_absent()
    {
        var ct = TestContext.Current.CancellationToken;
        var persistence = Substitute.For<IWorkItemPersistence>();
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        Dictionary<string, string?>? auditDetails = null;
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-id"));
        auditAppender
            .AppendAsync(
                Arg.Any<Guid>(),
                "notification-sent",
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string?>>(d => auditDetails = d),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);

        // Legacy item predating RA-219: no applicationReference on the payload.
        var workItem = BuildWorkItem(applicationReference: null);
        var sut = BuildSut(persistence, notifyClient, auditAppender);

        await sut.OnAllTasksCompletedAsync(workItem, "submitted", s_user, ct);

        Assert.NotNull(captured);
        Assert.Equal(workItem.Id.ToString(), captured!["reference"]);
        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "DulyMade",
                "op@example.com",
                Arg.Any<Dictionary<string, string>>(),
                workItem.Id.ToString(),
                cancellationToken: ct
            );
        Assert.NotNull(auditDetails);
        Assert.Equal(workItem.Id.ToString(), auditDetails!["reference"]);
    }
}
