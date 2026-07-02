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
    private static readonly ClaimsPrincipal s_user = new(
        new ClaimsIdentity(
            [new Claim("user:id", "user-1"), new Claim("user:name", "Alice")],
            "test"
        )
    );

    private static WorkItem BuildWorkItem(
        string stateId = "submitted",
        string? operatorEmail = "op@example.com",
        WorkItemSlaClock? slaClock = null,
        IEnumerable<WorkItemNote>? notes = null,
        bool nullNotes = false
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

        return new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            Payload = payload,
            SlaClock = slaClock,
            Notes = nullNotes ? null! : (notes?.ToList() ?? new List<WorkItemNote>()),
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType()),
            TemplateVersion = "v3",
        };
    }

    private static WorkItemNote Note(string text, DateTime createdAt, string? taskId = null) =>
        new()
        {
            Text = text,
            CreatedAt = createdAt,
            TaskId = taskId,
        };

    private static ReAccreditationNotificationHook BuildSut(
        INotifyClient notifyClient,
        IWorkItemAuditAppender auditAppender
    ) => new(notifyClient, auditAppender, NullLogger<ReAccreditationNotificationHook>.Instance);

    // ─────────────────────────── OnSubmittedAsync ───────────────────────────

    [Fact]
    public async Task OnSubmittedAsync_sends_SubmissionConfirmation_and_records_sent_audit_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-id-1"));

        var workItem = BuildWorkItem();
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "SubmissionConfirmation",
                "op@example.com",
                Arg.Any<Dictionary<string, string>>(),
                workItem.Id.ToString(),
                ct
            );

        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-sent",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["templateKey"] == "SubmissionConfirmation"
                    && d["providerMessageId"] == "msg-id-1"
                ),
                s_user,
                ct
            );
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
            TemplateVersion = "v3",
        };
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        await notifyClient
            .DidNotReceiveWithAnyArgs()
            .SendEmailAsync(default!, default!, default!, default!, ct);
        await auditAppender
            .DidNotReceiveWithAnyArgs()
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

        await notifyClient
            .DidNotReceiveWithAnyArgs()
            .SendEmailAsync(default!, default!, default!, default!, ct);
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
    public async Task OnSubmittedAsync_records_failed_audit_entry_when_notify_returns_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Failure("503 Service Unavailable"));

        var workItem = BuildWorkItem();
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-failed",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["templateKey"] == "SubmissionConfirmation"
                    && d["errorMessage"] == "503 Service Unavailable"
                ),
                s_user,
                ct
            );
    }

    // ─────────────────────────── OnActionAppliedAsync ───────────────────────

    [Theory]
    [InlineData("payment-received", "AssessmentInProgress")]
    [InlineData("sla-extend", "SlaExtended")]
    public async Task OnActionAppliedAsync_sends_correct_template_for_action(
        string actionId,
        string expectedTemplateKey
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-id"));

        var workItem = BuildWorkItem();
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, actionId, fromStateId: "submitted", s_user, ct);

        await notifyClient
            .Received(1)
            .SendEmailAsync(
                expectedTemplateKey,
                "op@example.com",
                Arg.Any<Dictionary<string, string>>(),
                workItem.Id.ToString(),
                ct
            );
    }

    [Theory]
    [InlineData("approve", "approved")]
    // RA-211: reject deliberately no longer sends any notification — see
    // OnActionAppliedAsync_reject_does_not_call_notify_client below.
    public async Task OnActionAppliedAsync_sends_Decision_template_with_correct_decision_value(
        string actionId,
        string toStateId
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? capturedPersonalisation = null;
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => capturedPersonalisation = d),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildWorkItem(stateId: toStateId);
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(
            workItem,
            actionId,
            fromStateId: "awaiting-decision",
            s_user,
            ct
        );

        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "Decision",
                "op@example.com",
                Arg.Any<Dictionary<string, string>>(),
                workItem.Id.ToString(),
                ct
            );

        Assert.NotNull(capturedPersonalisation);
        var expectedDecision = actionId == "approve" ? "Approved" : "Rejected";
        Assert.Equal(expectedDecision, capturedPersonalisation!["decision"]);
        // RA-203: decision_notes must always be present for the Decision
        // template. With no work-item-level notes on this item it is empty.
        Assert.True(capturedPersonalisation.ContainsKey("decision_notes"));
        Assert.Equal(string.Empty, capturedPersonalisation["decision_notes"]);
    }

    [Theory]
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

        await notifyClient
            .DidNotReceiveWithAnyArgs()
            .SendEmailAsync(default!, default!, default!, default!, ct);
        await auditAppender
            .DidNotReceiveWithAnyArgs()
            .AppendAsync(default, default!, default!, default!, default!, ct);
    }

    // RA-211: unlike assign/unassign (never mapped), reject was previously
    // mapped to the Decision template — this asserts the mapping was
    // actively removed, not just "never existed", so a future accidental
    // re-add is caught. The reject transition itself (and its own
    // action-applied audit entry) is a WorkItemService concern, exercised
    // separately in ReAccreditationLifecycleTests; this hook only owns the
    // notification side-effect and must produce none for reject.
    [Fact]
    public async Task OnActionAppliedAsync_reject_does_not_call_notify_client()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();

        var workItem = BuildWorkItem(stateId: "rejected");
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, "reject", "awaiting-decision", s_user, ct);

        await notifyClient
            .DidNotReceiveWithAnyArgs()
            .SendEmailAsync(default!, default!, default!, default!, ct);
        await auditAppender
            .DidNotReceiveWithAnyArgs()
            .AppendAsync(default, default!, default!, default!, default!, ct);
    }

    // ─────── RA-132: Decision personalisation extras ───────

    [Fact]
    public async Task OnActionAppliedAsync_includes_accreditation_id_and_start_date_in_Decision_personalisation()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildWorkItem(stateId: "approved");
        workItem.Payload["accreditationId"] = "RA-12345678";
        workItem.Payload["accreditationStartDate"] = new DateTime(
            2025,
            2,
            3,
            0,
            0,
            0,
            DateTimeKind.Utc
        );

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
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildWorkItem(stateId: "approved");
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, "approve", "awaiting-decision", s_user, ct);

        Assert.NotNull(captured);
        Assert.DoesNotContain("accreditation_id", captured!.Keys);
        Assert.DoesNotContain("accreditation_start_date", captured.Keys);
    }

    // ─────── RA-203: Decision personalisation (decision_notes) ───────

    [Theory]
    [InlineData("approve")]
    public async Task OnActionAppliedAsync_uses_latest_work_item_level_note_as_decision_notes(
        string actionId
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg"));

        // Out-of-order CreatedAt so the OrderByDescending branch is exercised:
        // the latest work-item-level note is the expected source.
        var workItem = BuildWorkItem(
            stateId: "approved",
            notes:
            [
                Note("Older rationale", new DateTime(2025, 10, 1, 0, 0, 0, DateTimeKind.Utc)),
                Note("Latest rationale", new DateTime(2025, 10, 9, 0, 0, 0, DateTimeKind.Utc)),
                Note("Middle rationale", new DateTime(2025, 10, 5, 0, 0, 0, DateTimeKind.Utc)),
            ]
        );
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, actionId, "awaiting-decision", s_user, ct);

        Assert.NotNull(captured);
        Assert.Equal("Latest rationale", captured!["decision_notes"]);
    }

    [Fact]
    public async Task OnActionAppliedAsync_ignores_task_scoped_notes_for_decision_notes()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg"));

        // The most recent note is task-scoped (TaskId set) and must be ignored;
        // the older work-item-level note is the expected source.
        var workItem = BuildWorkItem(
            stateId: "approved",
            notes:
            [
                Note("Work-item rationale", new DateTime(2025, 10, 1, 0, 0, 0, DateTimeKind.Utc)),
                Note(
                    "Task comment",
                    new DateTime(2025, 10, 9, 0, 0, 0, DateTimeKind.Utc),
                    taskId: "check-docs"
                ),
            ]
        );
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, "approve", "awaiting-decision", s_user, ct);

        Assert.NotNull(captured);
        Assert.Equal("Work-item rationale", captured!["decision_notes"]);
    }

    [Fact]
    public async Task OnActionAppliedAsync_sets_empty_decision_notes_when_notes_collection_is_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildWorkItem(stateId: "approved", nullNotes: true);
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, "approve", "awaiting-decision", s_user, ct);

        Assert.NotNull(captured);
        Assert.True(captured!.ContainsKey("decision_notes"));
        Assert.Equal(string.Empty, captured["decision_notes"]);
    }

    [Fact]
    public async Task OnActionAppliedAsync_sets_empty_decision_notes_when_no_work_item_level_notes()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildWorkItem(
            stateId: "approved",
            notes:
            [
                Note(
                    "Task comment",
                    new DateTime(2025, 10, 9, 0, 0, 0, DateTimeKind.Utc),
                    taskId: "check-docs"
                ),
            ]
        );
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, "approve", "awaiting-decision", s_user, ct);

        Assert.NotNull(captured);
        Assert.True(captured!.ContainsKey("decision_notes"));
        Assert.Equal(string.Empty, captured["decision_notes"]);
    }

    // ─────── RA-201: SlaExtended personalisation (sla_deadline) ───────

    [Fact]
    public async Task OnActionAppliedAsync_includes_sla_deadline_in_SlaExtended_personalisation()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg"));

        // Clock started 2025-10-09 UTC + 84 days (12 weeks) => 2026-01-01.
        var slaClock = new WorkItemSlaClock
        {
            StartedAt = new DateTime(2025, 10, 9, 9, 30, 0, DateTimeKind.Utc),
            TargetDuration = TimeSpan.FromDays(84),
        };
        var workItem = BuildWorkItem(slaClock: slaClock);
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(
            workItem,
            "sla-extend",
            "assessment-in-progress",
            s_user,
            ct
        );

        Assert.NotNull(captured);
        Assert.Equal("1 January 2026", captured!["sla_deadline"]);
        Assert.Equal("Acme Ltd", captured["organisation_name"]);
        Assert.Equal("EX-001", captured["registration_number"]);
        Assert.Equal(workItem.Id.ToString(), captured["reference"]);
    }

    [Fact]
    public async Task OnActionAppliedAsync_omits_sla_deadline_when_clock_absent()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildWorkItem(slaClock: null);
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(
            workItem,
            "sla-extend",
            "assessment-in-progress",
            s_user,
            ct
        );

        Assert.NotNull(captured);
        Assert.DoesNotContain("sla_deadline", captured!.Keys);
    }

    // ─────── RA-204: Withdrawn notification ───────

    [Theory]
    [InlineData("withdraw")]
    [InlineData("withdraw-during-duly-made")]
    [InlineData("withdraw-during-assessment")]
    [InlineData("withdraw-during-decision")]
    public async Task OnActionAppliedAsync_sends_Withdrawn_template_and_records_sent_audit_entry(
        string actionId
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-withdrawn"));

        var workItem = BuildWorkItem(stateId: "withdrawn");
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, actionId, fromStateId: "submitted", s_user, ct);

        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "Withdrawn",
                "op@example.com",
                Arg.Any<Dictionary<string, string>>(),
                workItem.Id.ToString(),
                ct
            );

        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-sent",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["templateKey"] == "Withdrawn" && d["providerMessageId"] == "msg-withdrawn"
                ),
                s_user,
                ct
            );
    }

    [Fact]
    public async Task OnActionAppliedAsync_records_skipped_audit_entry_for_Withdrawn_when_operator_email_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();

        var workItem = BuildWorkItem(stateId: "withdrawn", operatorEmail: null);
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, "withdraw", fromStateId: "submitted", s_user, ct);

        await notifyClient
            .DidNotReceiveWithAnyArgs()
            .SendEmailAsync(default!, default!, default!, default!, ct);
        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-skipped",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d => d["templateKey"] == "Withdrawn"),
                s_user,
                ct
            );
    }

    [Fact]
    public async Task OnActionAppliedAsync_records_failed_audit_entry_for_Withdrawn_when_notify_returns_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Failure("503 Service Unavailable"));

        var workItem = BuildWorkItem(stateId: "withdrawn");
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, "withdraw", fromStateId: "submitted", s_user, ct);

        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-failed",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["templateKey"] == "Withdrawn"
                    && d["errorMessage"] == "503 Service Unavailable"
                ),
                s_user,
                ct
            );
    }

    [Fact]
    public async Task OnActionAppliedAsync_uses_latest_work_item_level_note_as_withdrawal_notes()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg"));

        // Out-of-order CreatedAt so the OrderByDescending branch is exercised:
        // the latest work-item-level note is the expected source.
        var workItem = BuildWorkItem(
            stateId: "withdrawn",
            notes:
            [
                Note("Older reason", new DateTime(2025, 10, 1, 0, 0, 0, DateTimeKind.Utc)),
                Note("Latest reason", new DateTime(2025, 10, 9, 0, 0, 0, DateTimeKind.Utc)),
                Note("Middle reason", new DateTime(2025, 10, 5, 0, 0, 0, DateTimeKind.Utc)),
            ]
        );
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, "withdraw", "submitted", s_user, ct);

        Assert.NotNull(captured);
        Assert.Equal("Latest reason", captured!["withdrawal_notes"]);
        // Base keys remain present for the Withdrawn template.
        Assert.Equal("Acme Ltd", captured["organisation_name"]);
        Assert.Equal("EX-001", captured["registration_number"]);
        Assert.Equal(workItem.Id.ToString(), captured["reference"]);
    }

    [Fact]
    public async Task OnActionAppliedAsync_ignores_task_scoped_notes_for_withdrawal_notes()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg"));

        // The most recent note is task-scoped (TaskId set) and must be ignored;
        // only a task-scoped note exists, so there is no work-item-level note
        // and withdrawal_notes falls back to empty.
        var workItem = BuildWorkItem(
            stateId: "withdrawn",
            notes:
            [
                Note(
                    "Task comment",
                    new DateTime(2025, 10, 9, 0, 0, 0, DateTimeKind.Utc),
                    taskId: "check-docs"
                ),
            ]
        );
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, "withdraw", "submitted", s_user, ct);

        Assert.NotNull(captured);
        Assert.True(captured!.ContainsKey("withdrawal_notes"));
        Assert.Equal(string.Empty, captured["withdrawal_notes"]);
    }

    [Fact]
    public async Task OnActionAppliedAsync_sets_empty_withdrawal_notes_when_no_notes()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildWorkItem(stateId: "withdrawn");
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, "withdraw", "submitted", s_user, ct);

        Assert.NotNull(captured);
        Assert.True(captured!.ContainsKey("withdrawal_notes"));
        Assert.Equal(string.Empty, captured["withdrawal_notes"]);
    }

    [Fact]
    public async Task OnActionAppliedAsync_sets_empty_withdrawal_notes_when_notes_collection_is_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg"));

        // Exercise the null-conditional (workItem.Notes is null) fallback arm
        // for the Withdrawn template.
        var workItem = BuildWorkItem(stateId: "withdrawn", nullNotes: true);
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, "withdraw", "submitted", s_user, ct);

        Assert.NotNull(captured);
        Assert.True(captured!.ContainsKey("withdrawal_notes"));
        Assert.Equal(string.Empty, captured["withdrawal_notes"]);
    }
}
