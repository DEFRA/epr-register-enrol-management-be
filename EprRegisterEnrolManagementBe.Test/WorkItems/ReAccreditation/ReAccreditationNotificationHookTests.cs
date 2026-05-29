using System.Security.Claims;
using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.ReAccreditation;

public class ReAccreditationNotificationHookTests
{
    private static readonly ClaimsPrincipal s_user = new(new ClaimsIdentity(
    [
        new Claim("user:id", "user-1"),
        new Claim("user:name", "Alice")
    ], "test"));

    private static WorkItem BuildWorkItem(
        string stateId = "submitted",
        string? operatorEmail = "op@example.com",
        WorkItemSlaClock? slaClock = null)
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

        return new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            Payload = payload,
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType()),
            TemplateVersion = "v3",
            SlaClock = slaClock
        };
    }

    private static ReAccreditationNotificationHook BuildSut(
        INotifyClient notifyClient,
        IWorkItemAuditAppender auditAppender) =>
        new(notifyClient,
            auditAppender,
            NullLogger<ReAccreditationNotificationHook>.Instance);

    // ─────────────────────────── OnSubmittedAsync ───────────────────────────

    [Fact]
    public async Task OnSubmittedAsync_sends_SubmissionConfirmation_and_records_sent_audit_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        notifyClient.SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(NotifySendResult.Success("msg-id-1"));

        var workItem = BuildWorkItem();
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        await notifyClient.Received(1).SendEmailAsync(
            "SubmissionConfirmation",
            "op@example.com",
            Arg.Any<Dictionary<string, string>>(),
            workItem.Id.ToString(),
            ct);

        await auditAppender.Received(1).AppendAsync(
            workItem.Id,
            "notification-sent",
            Arg.Any<string>(),
            Arg.Is<Dictionary<string, string?>>(d => d["templateKey"] == "SubmissionConfirmation"
                                                     && d["providerMessageId"] == "msg-id-1"),
            s_user,
            ct);
    }

    [Fact]
    public async Task OnSubmittedAsync_skips_non_re_accreditation_work_items()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();

        var workItem = new WorkItem
        {
            TypeId = "some-other-type",
            StateId = "submitted",
            Payload = new BsonDocument { ["operatorEmail"] = "op@example.com" },
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType()),
            TemplateVersion = "v3"
        };
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        await notifyClient.DidNotReceiveWithAnyArgs()
            .SendEmailAsync(default!, default!, default!, default!, ct);
        await auditAppender.DidNotReceiveWithAnyArgs()
            .AppendAsync(default, default!, default!, default!, default!, ct);
    }

    [Fact]
    public async Task OnSubmittedAsync_records_skipped_audit_entry_when_operator_email_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();

        var workItem = BuildWorkItem(operatorEmail: null);
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        await notifyClient.DidNotReceiveWithAnyArgs()
            .SendEmailAsync(default!, default!, default!, default!, ct);
        await auditAppender.Received(1).AppendAsync(
            workItem.Id,
            "notification-skipped",
            Arg.Any<string>(),
            Arg.Any<Dictionary<string, string?>>(),
            s_user,
            ct);
    }

    [Fact]
    public async Task OnSubmittedAsync_records_failed_audit_entry_when_notify_returns_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        notifyClient.SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(NotifySendResult.Failure("503 Service Unavailable"));

        var workItem = BuildWorkItem();
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        await auditAppender.Received(1).AppendAsync(
            workItem.Id,
            "notification-failed",
            Arg.Any<string>(),
            Arg.Is<Dictionary<string, string?>>(d =>
                d["templateKey"] == "SubmissionConfirmation"
                && d["errorMessage"] == "503 Service Unavailable"),
            s_user,
            ct);
    }

    // ─────────────────────────── OnActionAppliedAsync ───────────────────────

    [Theory]
    [InlineData("duly-make", "DulyMade")]
    [InlineData("payment-received", "AssessmentInProgress")]
    public async Task OnActionAppliedAsync_sends_correct_template_for_action(
        string actionId, string expectedTemplateKey)
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        notifyClient.SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(NotifySendResult.Success("msg-id"));

        var workItem = BuildWorkItem();
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, actionId, fromStateId: "submitted", s_user, ct);

        await notifyClient.Received(1).SendEmailAsync(
            expectedTemplateKey,
            "op@example.com",
            Arg.Any<Dictionary<string, string>>(),
            workItem.Id.ToString(),
            ct);
    }

    [Theory]
    [InlineData("approve", "approved")]
    [InlineData("reject", "rejected")]
    public async Task OnActionAppliedAsync_sends_Decision_template_with_correct_decision_value(
        string actionId, string toStateId)
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? capturedPersonalisation = null;
        notifyClient.SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => capturedPersonalisation = d),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildWorkItem(stateId: toStateId);
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, actionId, fromStateId: "awaiting-decision", s_user, ct);

        await notifyClient.Received(1).SendEmailAsync(
            "Decision",
            "op@example.com",
            Arg.Any<Dictionary<string, string>>(),
            workItem.Id.ToString(),
            ct);

        Assert.NotNull(capturedPersonalisation);
        var expectedDecision = actionId == "approve" ? "Approved" : "Rejected";
        Assert.Equal(expectedDecision, capturedPersonalisation!["decision"]);
    }

    [Theory]
    [InlineData("withdraw")]
    [InlineData("assign")]
    [InlineData("unassign")]
    public async Task OnActionAppliedAsync_ignores_unmapped_actions(string actionId)
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();

        var workItem = BuildWorkItem();
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, actionId, fromStateId: "submitted", s_user, ct);

        await notifyClient.DidNotReceiveWithAnyArgs()
            .SendEmailAsync(default!, default!, default!, default!, ct);
        await auditAppender.DidNotReceiveWithAnyArgs()
            .AppendAsync(default, default!, default!, default!, default!, ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_records_sent_audit_entry_on_success()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        notifyClient.SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(NotifySendResult.Success("msg-id-2"));

        var workItem = BuildWorkItem();
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, "duly-make", fromStateId: "submitted", s_user, ct);

        await auditAppender.Received(1).AppendAsync(
            workItem.Id,
            "notification-sent",
            Arg.Any<string>(),
            Arg.Is<Dictionary<string, string?>>(d =>
                d["templateKey"] == "DulyMade" && d["providerMessageId"] == "msg-id-2"),
            s_user,
            ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_records_failed_audit_entry_and_does_not_throw_on_notify_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        notifyClient.SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(NotifySendResult.Failure("timeout"));

        var workItem = BuildWorkItem();
        var sut = BuildSut(notifyClient, auditAppender);

        // Must not throw — notification failure must not unwind the originating action.
        await sut.OnActionAppliedAsync(workItem, "duly-make", fromStateId: "submitted", s_user, ct);

        await auditAppender.Received(1).AppendAsync(
            workItem.Id,
            "notification-failed",
            Arg.Any<string>(),
            Arg.Is<Dictionary<string, string?>>(d =>
                d["templateKey"] == "DulyMade" && d["errorMessage"] == "timeout"),
            s_user,
            ct);
    }

    [Fact]
    public async Task personalisation_includes_organisation_name_and_registration_number()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient.SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(NotifySendResult.Success("id"));

        var workItem = BuildWorkItem();
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, "duly-make", "submitted", s_user, ct);

        Assert.NotNull(captured);
        Assert.Equal("Acme Ltd", captured!["organisation_name"]);
        Assert.Equal("EX-001", captured["registration_number"]);
        Assert.Equal(workItem.Id.ToString(), captured["reference"]);
    }

    // ─────── RA-132: Decision personalisation extras ───────

    [Fact]
    public async Task OnActionAppliedAsync_includes_accreditation_id_and_start_date_in_Decision_personalisation()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient.SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildWorkItem(stateId: "approved");
        workItem.Payload["accreditationId"] = "RA-12345678";
        workItem.Payload["accreditationStartDate"] = new DateTime(2025, 2, 3, 0, 0, 0, DateTimeKind.Utc);

        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, "approve", "assessment-in-progress", s_user, ct);

        Assert.NotNull(captured);
        Assert.Equal("RA-12345678", captured!["accreditation_id"]);
        Assert.Equal("2025-02-03", captured["accreditation_start_date"]);
    }

    [Fact]
    public async Task OnActionAppliedAsync_omits_accreditation_keys_when_payload_lacks_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient.SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildWorkItem(stateId: "rejected");
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, "reject", "awaiting-decision", s_user, ct);

        Assert.NotNull(captured);
        Assert.DoesNotContain("accreditation_id", captured!.Keys);
        Assert.DoesNotContain("accreditation_start_date", captured.Keys);
    }

    // ─────── SlaExtended personalisation ───────

    [Fact]
    public async Task OnActionAppliedAsync_includes_sla_deadline_in_SlaExtended_personalisation()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient.SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(NotifySendResult.Success("msg"));

        var slaClock = new WorkItemSlaClock
        {
            StartedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            TargetDuration = TimeSpan.FromDays(98) // extended from default 84 days
        };
        var workItem = BuildWorkItem(stateId: "assessment-in-progress", slaClock: slaClock);
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, "sla-extend", "assessment-in-progress", s_user, ct);

        await notifyClient.Received(1).SendEmailAsync(
            "SlaExtended",
            "op@example.com",
            Arg.Any<Dictionary<string, string>>(),
            workItem.Id.ToString(),
            ct);

        Assert.NotNull(captured);
        Assert.Equal("2026-04-09", captured!["sla_deadline"]);
    }

    [Fact]
    public async Task OnActionAppliedAsync_skips_SlaExtended_when_sla_clock_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();

        var workItem = BuildWorkItem(stateId: "assessment-in-progress", slaClock: null);
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, "sla-extend", "assessment-in-progress", s_user, ct);

        await notifyClient.DidNotReceiveWithAnyArgs()
            .SendEmailAsync(default!, default!, default!, default!, ct);
        await auditAppender.Received(1).AppendAsync(
            workItem.Id,
            "notification-skipped",
            Arg.Any<string>(),
            Arg.Is<Dictionary<string, string?>>(d =>
                d["templateKey"] == "SlaExtended"
                && d["reason"] == "missing-sla-clock"),
            s_user,
            ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_omits_sla_deadline_for_non_SlaExtended_templates()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient.SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(NotifySendResult.Success("msg"));

        var slaClock = new WorkItemSlaClock
        {
            StartedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var workItem = BuildWorkItem(slaClock: slaClock);
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, "duly-make", "submitted", s_user, ct);

        Assert.NotNull(captured);
        Assert.DoesNotContain("sla_deadline", captured!.Keys);
    }

    // ─────── Decision personalisation: decision_notes ───────

    [Theory]
    [InlineData("approve", "approved", "Approved — operator has fully demonstrated capacity for the requested tonnages.")]
    [InlineData("reject", "rejected", "Rejected — insufficient evidence of downstream reprocessing capacity.")]
    public async Task OnActionAppliedAsync_includes_decision_notes_from_latest_rationale_note(
        string actionId, string toStateId, string rationale)
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient.SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildWorkItem(stateId: toStateId);
        // Older non-rationale note must not be selected.
        workItem.Notes.Add(new WorkItemNote
        {
            Text = "Caseworker chase-up to operator on 2 Jan",
            CreatedAt = new DateTime(2026, 1, 2, 10, 0, 0, DateTimeKind.Utc)
        });
        // Older rationale note must lose to the newer one.
        workItem.Notes.Add(new WorkItemNote
        {
            Text = "[decision-rationale] superseded draft rationale",
            CreatedAt = new DateTime(2026, 1, 3, 10, 0, 0, DateTimeKind.Utc)
        });
        workItem.Notes.Add(new WorkItemNote
        {
            Text = $"[decision-rationale] {rationale}",
            CreatedAt = new DateTime(2026, 1, 4, 10, 0, 0, DateTimeKind.Utc)
        });

        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, actionId, "awaiting-decision", s_user, ct);

        Assert.NotNull(captured);
        Assert.Equal(rationale, captured!["decision_notes"]);
    }

    [Fact]
    public async Task OnActionAppliedAsync_falls_back_to_default_decision_notes_when_no_rationale_recorded()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient.SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildWorkItem(stateId: "approved");
        // Non-rationale note present so we exercise the "no matching note"
        // path rather than the "no notes at all" path.
        workItem.Notes.Add(new WorkItemNote
        {
            Text = "Ad-hoc caseworker note",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, "approve", "assessment-in-progress", s_user, ct);

        Assert.NotNull(captured);
        Assert.Equal("No additional notes recorded.", captured!["decision_notes"]);

        // Send must still happen — decision_notes fallback never blocks
        // the critical operator email.
        await notifyClient.Received(1).SendEmailAsync(
            "Decision",
            "op@example.com",
            Arg.Any<Dictionary<string, string>>(),
            workItem.Id.ToString(),
            ct);
        await auditAppender.Received(1).AppendAsync(
            workItem.Id,
            "notification-sent",
            Arg.Any<string>(),
            Arg.Is<Dictionary<string, string?>>(d => d["templateKey"] == "Decision"),
            s_user,
            ct);
    }

    [Fact]
    public async Task OnActionAppliedAsync_omits_decision_notes_for_non_Decision_templates()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient.SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildWorkItem();
        workItem.Notes.Add(new WorkItemNote
        {
            Text = "[decision-rationale] should not leak into DulyMade",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, "duly-make", "submitted", s_user, ct);

        Assert.NotNull(captured);
        Assert.DoesNotContain("decision_notes", captured!.Keys);
    }
}
