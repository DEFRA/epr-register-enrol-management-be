using System.Security.Claims;
using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    // RA-248: human-facing application reference stamped on the payload by the
    // core WorkItemService; expected in the ((reference)) Notify placeholder.
    private const string ApplicationReference = "RA-000123456";

    private static WorkItem BuildWorkItem(
        string stateId = "submitted",
        string? operatorEmail = "op@example.com",
        WorkItemSlaClock? slaClock = null,
        IEnumerable<WorkItemNote>? notes = null,
        bool nullNotes = false,
        Nation? nation = null,
        bool includeNation = false,
        string? assignedToName = null,
        string? assignedBy = null,
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
        // ReAccreditationNationRoutingHook stamps payload.nation as the enum
        // name string; mirror that here. includeNation with a null nation
        // stamps the BSON null the resolver treats as "no nation", which a
        // plain `nation is not null` check could not express.
        if (includeNation || nation is not null)
        {
            payload["nation"] = nation is null ? BsonNull.Value : nation.Value.ToString();
        }
        if (applicationReference is not null)
        {
            payload["applicationReference"] = applicationReference;
        }

        return new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            Payload = payload,
            SlaClock = slaClock,
            Notes = nullNotes ? null! : (notes?.ToList() ?? new List<WorkItemNote>()),
            AssignedToName = assignedToName,
            AssignedBy = assignedBy,
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType()),
            TemplateVersion = "v3",
        };
    }

    private static IRegulatorMailboxResolver ResolverReturning(string? mailbox)
    {
        var resolver = Substitute.For<IRegulatorMailboxResolver>();
        resolver.Resolve(Arg.Any<Nation?>()).Returns(mailbox);
        return resolver;
    }

    private static WorkItemNote Note(string text, DateTime createdAt) =>
        new()
        {
            Text = text,
            CreatedAt = createdAt,
        };

    private static ReAccreditationNotificationHook BuildSut(
        INotifyClient notifyClient,
        IWorkItemAuditAppender auditAppender,
        IRegulatorMailboxResolver? regulatorMailboxResolver = null,
        WorkItem? persistedWorkItem = null,
        string? operatorServiceBaseUrl = null
    )
    {
        // Default resolver returns null so the RA-240 regulator send that
        // OnSubmittedAsync now also fires is skipped in the operator-facing
        // lifecycle tests below — those assert only the operator email.
        // RA-240 / RA-237 tests pass their own resolver.
        var resolver = regulatorMailboxResolver ?? Substitute.For<IRegulatorMailboxResolver>();

        // The regulator send re-reads the persisted work item to resolve the
        // routed nation (submission-ordering caveat). Return the supplied item
        // (typically the same one under test, carrying payload.nation) so the
        // re-read sees the stamped nation; null exercises the fallback arm.
        var persistence = Substitute.For<IWorkItemPersistence>();
        persistence
            .GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(persistedWorkItem);

        // RA-291: null models an environment with no OperatorService section
        // bound at all — distinct from a bound-but-empty BaseUrl, and both
        // must degrade to an empty operator_service_link rather than throwing.
        var operatorServiceOptions = operatorServiceBaseUrl is null
            ? null
            : Options.Create(new OperatorServiceConfig { BaseUrl = operatorServiceBaseUrl });

        return new(
            notifyClient,
            auditAppender,
            resolver,
            persistence,
            NullLogger<ReAccreditationNotificationHook>.Instance,
            operatorServiceOptions
        );
    }

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
                cancellationToken: Arg.Any<CancellationToken>()
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
                ApplicationReference,
                cancellationToken: ct
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
            .SendEmailAsync(default!, default!, default!, default!, default!, ct);
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
            .SendEmailAsync(default!, default!, default!, default!, default!, ct);
        // The operator-facing SubmissionConfirmation is skipped for the missing
        // operator email. (The RA-240 regulator send is also skipped here — the
        // default resolver returns no mailbox — with reason
        // missing-regulator-mailbox; scoped-out via the templateKey predicate.)
        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-skipped",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["templateKey"] == "SubmissionConfirmation"
                    && d["reason"] == "missing-operator-email"
                ),
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
                cancellationToken: Arg.Any<CancellationToken>()
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
    [InlineData("query-during-duly-making", "Queried")]
    [InlineData("query-during-duly-made", "Queried")]
    [InlineData("query-during-assessment", "Queried")]
    [InlineData("query-during-decision", "Queried")]
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
                cancellationToken: Arg.Any<CancellationToken>()
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
                ApplicationReference,
                cancellationToken: ct
            );
    }

    // ─────── RA-211: region resolved from payload.Nation ───────

    [Fact]
    public async Task OnActionAppliedAsync_passes_the_payloads_nation_as_region()
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
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-id"));

        var workItem = BuildWorkItem(nation: Nation.Wales);
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(
            workItem,
            "payment-received",
            fromStateId: "duly-made",
            s_user,
            ct
        );

        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "AssessmentInProgress",
                "op@example.com",
                Arg.Any<Dictionary<string, string>>(),
                ApplicationReference,
                "Wales",
                ct
            );
    }

    [Fact]
    public async Task OnActionAppliedAsync_passes_null_region_when_payload_has_no_nation()
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
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-id"));

        var workItem = BuildWorkItem();
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(
            workItem,
            "payment-received",
            fromStateId: "duly-made",
            s_user,
            ct
        );

        // No nation on the payload: region falls through as null so
        // GovukNotifyClient's NotifyConfig.DefaultReplyToId fallback applies
        // rather than a bogus/empty region string.
        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "AssessmentInProgress",
                "op@example.com",
                Arg.Any<Dictionary<string, string>>(),
                ApplicationReference,
                cancellationToken: ct
            );
    }

    // ─── RA-291 (AC06): operator-service link in the Queried email ───

    [Theory]
    // Configured: the link is passed through verbatim (trimmed).
    [InlineData("https://operator.example.gov.uk", "https://operator.example.gov.uk")]
    [InlineData("  https://operator.example.gov.uk  ", "https://operator.example.gov.uk")]
    // Unset / blank / section absent entirely: the key is still supplied with
    // an empty value. Notify 400s a send whose template references a
    // placeholder the caller omitted, so a config gap must not break querying.
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public async Task OnActionAppliedAsync_always_supplies_operator_service_link_for_queried(
        string? configuredBaseUrl,
        string expectedLink
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
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-queried"));

        var workItem = BuildWorkItem(stateId: "queried");
        var sut = BuildSut(notifyClient, auditAppender, operatorServiceBaseUrl: configuredBaseUrl);

        await sut.OnActionAppliedAsync(
            workItem,
            "query-during-duly-making",
            fromStateId: "submitted",
            s_user,
            ct
        );

        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "Queried",
                "op@example.com",
                Arg.Is<Dictionary<string, string>>(p =>
                    p.ContainsKey("operator_service_link")
                    && p["operator_service_link"] == expectedLink
                ),
                ApplicationReference,
                cancellationToken: ct
            );
    }

    // ─── RA-291: the query reason reaches the operator's email ───

    [Fact]
    public async Task OnActionAppliedAsync_sends_the_current_query_reason_for_queried()
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
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-queried"));

        var workItem = BuildWorkItem(stateId: "queried");
        workItem.Payload!["currentQuery"] = new BsonDocument
        {
            ["reason"] = "The tonnage figures do not reconcile.",
            ["sections"] = new BsonArray { "prn-tonnage", "business-plan" },
        };
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(
            workItem,
            "query-during-duly-making",
            fromStateId: "submitted",
            s_user,
            ct
        );

        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "Queried",
                "op@example.com",
                Arg.Is<Dictionary<string, string>>(p =>
                    p["query_reason"] == "The tonnage figures do not reconcile."
                    // Sections are recorded on the work item and the audit log,
                    // but deliberately not sent as personalisation: every key
                    // must exist in the live template or Notify 400s the send.
                    && !p.ContainsKey("query_sections")
                ),
                ApplicationReference,
                cancellationToken: ct
            );
    }

    [Theory]
    // No current query at all (a queried transition applied outside
    // ReAccreditationQueryService, or a legacy item) ...
    [InlineData(null)]
    // ... or one with a blank reason. Both degrade to an empty value: omitting
    // the key would make Notify 400 the send, and throwing would fail a
    // notification that must never unwind the query.
    [InlineData("")]
    [InlineData("   ")]
    public async Task OnActionAppliedAsync_sends_an_empty_query_reason_when_none_is_recorded(
        string? recordedReason
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
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-queried"));

        var workItem = BuildWorkItem(stateId: "queried");
        if (recordedReason is not null)
        {
            workItem.Payload!["currentQuery"] = new BsonDocument
            {
                ["reason"] = recordedReason,
            };
        }
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(
            workItem,
            "query-during-duly-making",
            fromStateId: "submitted",
            s_user,
            ct
        );

        // The send still happens — the query itself is already committed.
        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "Queried",
                "op@example.com",
                Arg.Is<Dictionary<string, string>>(p =>
                    p.ContainsKey("query_reason") && p["query_reason"] == ""
                ),
                ApplicationReference,
                cancellationToken: ct
            );
    }

    [Fact]
    public async Task OnActionAppliedAsync_does_not_add_query_reason_to_other_templates()
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
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-1"));

        var workItem = BuildWorkItem(stateId: "withdrawn");
        workItem.Payload!["currentQuery"] = new BsonDocument
        {
            ["reason"] = "An earlier query, since answered.",
        };
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(
            workItem,
            "withdraw",
            fromStateId: "submitted",
            s_user,
            ct
        );

        // A stale current query must not leak into unrelated templates —
        // Notify rejects surplus keys.
        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "Withdrawn",
                "op@example.com",
                Arg.Is<Dictionary<string, string>>(p => !p.ContainsKey("query_reason")),
                ApplicationReference,
                cancellationToken: ct
            );
    }

    [Fact]
    public async Task OnActionAppliedAsync_does_not_add_operator_service_link_to_other_templates()
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
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-1"));

        var workItem = BuildWorkItem(stateId: "assessment-in-progress");
        var sut = BuildSut(
            notifyClient, auditAppender, operatorServiceBaseUrl: "https://operator.example.gov.uk");

        await sut.OnActionAppliedAsync(
            workItem,
            "payment-received",
            fromStateId: "duly-made",
            s_user,
            ct
        );

        // Notify rejects surplus personalisation keys as well as missing
        // ones, so the link must not leak onto templates that do not
        // reference it.
        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "AssessmentInProgress",
                "op@example.com",
                Arg.Is<Dictionary<string, string>>(p => !p.ContainsKey("operator_service_link")),
                ApplicationReference,
                cancellationToken: ct
            );
    }

    // ─────── RA-211: queried transition sends the Queried template ───────

    [Theory]
    [InlineData("query-during-duly-making")]
    [InlineData("query-during-duly-made")]
    [InlineData("query-during-assessment")]
    [InlineData("query-during-decision")]
    public async Task OnActionAppliedAsync_records_notification_sent_audit_entry_for_queried(
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
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-queried"));

        var workItem = BuildWorkItem(stateId: "queried");
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(
            workItem,
            actionId,
            fromStateId: "assessment-in-progress",
            s_user,
            ct
        );

        // Exactly one send, exactly one notification-sent entry — same
        // contract as every other lifecycle template.
        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "Queried",
                "op@example.com",
                Arg.Any<Dictionary<string, string>>(),
                ApplicationReference,
                cancellationToken: ct
            );
        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-sent",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["templateKey"] == "Queried" && d["providerMessageId"] == "msg-queried"
                ),
                s_user,
                ct
            );
    }

    [Fact]
    public async Task OnActionAppliedAsync_records_notification_failed_audit_entry_for_queried_after_retries_exhausted()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        // INotifyClient.SendEmailAsync already encapsulates the 3-attempt
        // retry (GovukNotifyClientTests covers that pipeline in isolation);
        // at the hook level a Failure result is what "retries exhausted"
        // looks like, and the hook's job is to turn that into the correct
        // audit entry rather than throwing.
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Failure("503 Service Unavailable"));

        var workItem = BuildWorkItem(stateId: "queried");
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(
            workItem,
            "query-during-decision",
            fromStateId: "awaiting-decision",
            s_user,
            ct
        );

        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-failed",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["templateKey"] == "Queried" && d["errorMessage"] == "503 Service Unavailable"
                ),
                s_user,
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
                cancellationToken: Arg.Any<CancellationToken>()
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
                ApplicationReference,
                cancellationToken: ct
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
            .SendEmailAsync(default!, default!, default!, default!, default!, ct);
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
            .SendEmailAsync(default!, default!, default!, default!, default!, ct);
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
                cancellationToken: Arg.Any<CancellationToken>()
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
                cancellationToken: Arg.Any<CancellationToken>()
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
                cancellationToken: Arg.Any<CancellationToken>()
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
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildWorkItem(stateId: "approved", nullNotes: true);
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
                cancellationToken: Arg.Any<CancellationToken>()
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
        Assert.Equal(ApplicationReference, captured["reference"]);
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
                cancellationToken: Arg.Any<CancellationToken>()
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
                cancellationToken: Arg.Any<CancellationToken>()
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
                ApplicationReference,
                cancellationToken: ct
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
            .SendEmailAsync(default!, default!, default!, default!, default!, ct);
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
                cancellationToken: Arg.Any<CancellationToken>()
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
                cancellationToken: Arg.Any<CancellationToken>()
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
        Assert.Equal(ApplicationReference, captured["reference"]);
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
                cancellationToken: Arg.Any<CancellationToken>()
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
                cancellationToken: Arg.Any<CancellationToken>()
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


    // ─────── RA-240: RegulatorSubmission notification ───────

    [Fact]
    public async Task OnSubmittedAsync_sends_RegulatorSubmission_to_resolved_mailbox_and_records_sent_audit_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? regulatorPersonalisation = null;
        notifyClient
            .SendEmailAsync(
                "RegulatorSubmission",
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => regulatorPersonalisation = d),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("reg-msg-1"));
        notifyClient
            .SendEmailAsync(
                "SubmissionConfirmation",
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("op-msg-1"));

        var workItem = BuildWorkItem(includeNation: true, nation: Nation.England);
        var sut = BuildSut(
            notifyClient,
            auditAppender,
            ResolverReturning("regulator@england.example.gov.uk"),
            persistedWorkItem: workItem
        );

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        // Both operator confirmation and regulator submission were sent. The
        // operator-facing send carries the RA-248 human-facing application
        // reference; the regulator-facing send is keyed on the work-item id.
        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "SubmissionConfirmation",
                "op@example.com",
                Arg.Any<Dictionary<string, string>>(),
                ApplicationReference,
                Arg.Any<string?>(),
                ct
            );
        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "RegulatorSubmission",
                "regulator@england.example.gov.uk",
                Arg.Any<Dictionary<string, string>>(),
                workItem.Id.ToString(),
                Arg.Any<string?>(),
                ct
            );

        Assert.NotNull(regulatorPersonalisation);
        Assert.Equal("Acme Ltd", regulatorPersonalisation!["organisation_name"]);
        Assert.Equal("EX-001", regulatorPersonalisation["registration_number"]);
        Assert.Equal(workItem.Id.ToString(), regulatorPersonalisation["reference"]);

        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-sent",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["templateKey"] == "RegulatorSubmission"
                    && d["recipient"] == "regulator@england.example.gov.uk"
                    && d["nation"] == "England"
                    && d["providerMessageId"] == "reg-msg-1"
                ),
                s_user,
                ct
            );
    }

    [Fact]
    public async Task OnSubmittedAsync_skips_RegulatorSubmission_when_nation_mailbox_unconfigured()
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
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("op-msg"));

        // Scotland is an unconfigured placeholder → resolver returns null.
        var workItem = BuildWorkItem(includeNation: true, nation: Nation.Scotland);
        var sut = BuildSut(
            notifyClient,
            auditAppender,
            ResolverReturning(null),
            persistedWorkItem: workItem
        );

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        // Operator confirmation still sent; regulator submission skipped.
        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "SubmissionConfirmation",
                "op@example.com",
                Arg.Any<Dictionary<string, string>>(),
                ApplicationReference,
                Arg.Any<string?>(),
                ct
            );
        await notifyClient
            .DidNotReceive()
            .SendEmailAsync(
                "RegulatorSubmission",
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                ct
            );

        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-skipped",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["templateKey"] == "RegulatorSubmission"
                    && d["reason"] == "missing-regulator-mailbox"
                    && d["nation"] == "Scotland"
                ),
                s_user,
                ct
            );
    }

    [Fact]
    public async Task OnSubmittedAsync_skips_RegulatorSubmission_when_nation_absent()
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
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("op-msg"));

        // No payload.nation at all → payload.Nation is null → resolver(null) is null.
        var workItem = BuildWorkItem();
        var sut = BuildSut(
            notifyClient,
            auditAppender,
            ResolverReturning(null),
            persistedWorkItem: workItem
        );

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        await notifyClient
            .DidNotReceive()
            .SendEmailAsync(
                "RegulatorSubmission",
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                ct
            );
        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-skipped",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["templateKey"] == "RegulatorSubmission"
                    && d["reason"] == "missing-regulator-mailbox"
                    && d["nation"] == null
                ),
                s_user,
                ct
            );
    }

    [Fact]
    public async Task OnSubmittedAsync_records_failed_audit_entry_when_RegulatorSubmission_send_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        notifyClient
            .SendEmailAsync(
                "SubmissionConfirmation",
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("op-msg"));
        // Post-retry failure surfaces from GovukNotifyClient as a Failure result.
        notifyClient
            .SendEmailAsync(
                "RegulatorSubmission",
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Failure("503 Service Unavailable"));

        var workItem = BuildWorkItem(includeNation: true, nation: Nation.England);
        var sut = BuildSut(
            notifyClient,
            auditAppender,
            ResolverReturning("regulator@england.example.gov.uk"),
            persistedWorkItem: workItem
        );

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-failed",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["templateKey"] == "RegulatorSubmission"
                    && d["errorMessage"] == "503 Service Unavailable"
                ),
                s_user,
                ct
            );
    }

    // ─────── RA-237: OfficerAssignment notification ───────

    [Theory]
    [InlineData(
        WorkItemAssignmentChange.Assigned,
        "assigned to an officer",
        "Bob Officer",
        "Bob Officer"
    )]
    [InlineData(
        WorkItemAssignmentChange.Reassigned,
        "reassigned to a different officer",
        "Carol Officer",
        "Carol Officer"
    )]
    [InlineData(WorkItemAssignmentChange.Unassigned, "unassigned", null, "")]
    public async Task OnAssignmentChangedAsync_sends_OfficerAssignment_with_correct_event_copy(
        WorkItemAssignmentChange change,
        string expectedEvent,
        string? assignedToName,
        string expectedOfficerName
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient
            .SendEmailAsync(
                "OfficerAssignment",
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("assign-msg"));

        // AssignedBy is deliberately left null even on assign/reassign: changed_by
        // must come from the acting principal, not this field, so a null here
        // cannot influence the assertion below.
        var workItem = BuildWorkItem(
            includeNation: true,
            nation: Nation.England,
            assignedToName: change == WorkItemAssignmentChange.Unassigned ? null : assignedToName,
            assignedBy: null
        );
        var sut = BuildSut(
            notifyClient,
            auditAppender,
            ResolverReturning("regulator@england.example.gov.uk"),
            persistedWorkItem: workItem
        );

        await sut.OnAssignmentChangedAsync(workItem, change, s_user, ct);

        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "OfficerAssignment",
                "regulator@england.example.gov.uk",
                Arg.Any<Dictionary<string, string>>(),
                workItem.Id.ToString(),
                Arg.Any<string?>(),
                ct
            );

        Assert.NotNull(captured);
        Assert.Equal("Acme Ltd", captured!["organisation_name"]);
        Assert.Equal("EX-001", captured["registration_number"]);
        Assert.Equal(workItem.Id.ToString(), captured["reference"]);
        Assert.Equal(expectedEvent, captured["assignment_event"]);
        Assert.Equal(expectedOfficerName, captured["officer_name"]);
        // s_user's user:name claim — the acting principal, present on every
        // change including unassign (where AssignedBy has already been cleared).
        Assert.Equal("Alice", captured["changed_by"]);

        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-sent",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["templateKey"] == "OfficerAssignment"
                    && d["providerMessageId"] == "assign-msg"
                ),
                s_user,
                ct
            );
    }

    [Fact]
    public async Task OnAssignmentChangedAsync_falls_back_to_user_id_when_name_claim_absent()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient
            .SendEmailAsync(
                "OfficerAssignment",
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("assign-msg"));

        // Principal carries an id but no display name — changed_by falls back to
        // the id rather than going blank, mirroring the audit log's
        // createdByName ?? createdBy precedence.
        var idOnlyUser = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("user:id", "user-9")], "test")
        );
        var workItem = BuildWorkItem(
            includeNation: true,
            nation: Nation.England,
            assignedToName: "Gus Officer"
        );
        var sut = BuildSut(
            notifyClient,
            auditAppender,
            ResolverReturning("regulator@england.example.gov.uk"),
            persistedWorkItem: workItem
        );

        await sut.OnAssignmentChangedAsync(
            workItem,
            WorkItemAssignmentChange.Assigned,
            idOnlyUser,
            ct
        );

        Assert.NotNull(captured);
        Assert.Equal("user-9", captured!["changed_by"]);
    }

    [Fact]
    public async Task OnAssignmentChangedAsync_sets_empty_changed_by_when_principal_has_no_claims()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient
            .SendEmailAsync(
                "OfficerAssignment",
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("assign-msg"));

        // Neither claim present — changed_by must still be an empty string, never
        // null, so Notify cannot 400 on a referenced placeholder.
        var anonymousUser = new ClaimsPrincipal(new ClaimsIdentity());
        var workItem = BuildWorkItem(
            includeNation: true,
            nation: Nation.England,
            assignedToName: "Hana Officer"
        );
        var sut = BuildSut(
            notifyClient,
            auditAppender,
            ResolverReturning("regulator@england.example.gov.uk"),
            persistedWorkItem: workItem
        );

        await sut.OnAssignmentChangedAsync(
            workItem,
            WorkItemAssignmentChange.Assigned,
            anonymousUser,
            ct
        );

        Assert.NotNull(captured);
        Assert.Equal(string.Empty, captured!["changed_by"]);
    }

    [Fact]
    public async Task OnAssignmentChangedAsync_skips_non_re_accreditation_work_items()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();

        var workItem = new WorkItem
        {
            TypeId = "some-other-type",
            StateId = "submitted",
            Payload = new BsonDocument { ["nation"] = "England" },
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType()),
            TemplateVersion = "v3",
        };
        var sut = BuildSut(
            notifyClient,
            auditAppender,
            ResolverReturning("regulator@england.example.gov.uk"),
            persistedWorkItem: workItem
        );

        await sut.OnAssignmentChangedAsync(workItem, WorkItemAssignmentChange.Assigned, s_user, ct);

        await notifyClient
            .DidNotReceiveWithAnyArgs()
            .SendEmailAsync(default!, default!, default!, default!, default!, ct);
        await auditAppender
            .DidNotReceiveWithAnyArgs()
            .AppendAsync(default, default!, default!, default!, default!, ct);
    }

    [Fact]
    public async Task OnAssignmentChangedAsync_skips_and_audits_when_nation_mailbox_unconfigured()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();

        var workItem = BuildWorkItem(
            includeNation: true,
            nation: Nation.Wales,
            assignedToName: "Dave Officer",
            assignedBy: "assigner-2"
        );
        var sut = BuildSut(
            notifyClient,
            auditAppender,
            ResolverReturning(null),
            persistedWorkItem: workItem
        );

        await sut.OnAssignmentChangedAsync(workItem, WorkItemAssignmentChange.Assigned, s_user, ct);

        await notifyClient
            .DidNotReceiveWithAnyArgs()
            .SendEmailAsync(default!, default!, default!, default!, default!, ct);
        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-skipped",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["templateKey"] == "OfficerAssignment"
                    && d["reason"] == "missing-regulator-mailbox"
                    && d["nation"] == "Wales"
                ),
                s_user,
                ct
            );
    }

    [Fact]
    public async Task OnAssignmentChangedAsync_skips_and_audits_when_nation_absent()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();

        // No payload.nation → payload.Nation null → resolver(null) null.
        var workItem = BuildWorkItem(assignedToName: "Eve Officer", assignedBy: "assigner-3");
        var sut = BuildSut(
            notifyClient,
            auditAppender,
            ResolverReturning(null),
            persistedWorkItem: workItem
        );

        await sut.OnAssignmentChangedAsync(
            workItem,
            WorkItemAssignmentChange.Reassigned,
            s_user,
            ct
        );

        await notifyClient
            .DidNotReceiveWithAnyArgs()
            .SendEmailAsync(default!, default!, default!, default!, default!, ct);
        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-skipped",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["templateKey"] == "OfficerAssignment"
                    && d["reason"] == "missing-regulator-mailbox"
                    && d["nation"] == null
                ),
                s_user,
                ct
            );
    }

    [Fact]
    public async Task OnAssignmentChangedAsync_records_failed_audit_entry_when_send_fails()
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
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Failure("503 Service Unavailable"));

        var workItem = BuildWorkItem(
            includeNation: true,
            nation: Nation.England,
            assignedToName: "Faye Officer",
            assignedBy: "assigner-4"
        );
        var sut = BuildSut(
            notifyClient,
            auditAppender,
            ResolverReturning("regulator@england.example.gov.uk"),
            persistedWorkItem: workItem
        );

        await sut.OnAssignmentChangedAsync(workItem, WorkItemAssignmentChange.Assigned, s_user, ct);

        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-failed",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["templateKey"] == "OfficerAssignment"
                    && d["errorMessage"] == "503 Service Unavailable"
                ),
                s_user,
                ct
            );
    }

    // ─────── RA-248: application reference drives the ((reference)) placeholder ───────

    [Fact]
    public async Task OnSubmittedAsync_uses_application_reference_for_personalisation_send_and_audit()
    {
        var ct = TestContext.Current.CancellationToken;
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
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-id-1"));
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

        // Default resolver returns no mailbox, so the RA-240 regulator send is
        // skipped and only the operator-facing SubmissionConfirmation reaches
        // Notify — keeping `captured` / `auditDetails` pinned to it.
        var workItem = BuildWorkItem();
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        // Personalisation placeholder carries the human-facing reference.
        Assert.NotNull(captured);
        Assert.Equal(ApplicationReference, captured!["reference"]);

        // The Notify send reference arg carries the same value.
        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "SubmissionConfirmation",
                "op@example.com",
                Arg.Any<Dictionary<string, string>>(),
                ApplicationReference,
                Arg.Any<string?>(),
                ct
            );

        // And so does the notification-sent audit detail.
        Assert.NotNull(auditDetails);
        Assert.Equal(ApplicationReference, auditDetails!["reference"]);
    }

    [Fact]
    public async Task OnSubmittedAsync_falls_back_to_work_item_id_when_application_reference_absent()
    {
        var ct = TestContext.Current.CancellationToken;
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
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-id-1"));
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

        // Legacy item predating RA-219: no applicationReference on the payload,
        // so the ((reference)) placeholder falls back to the work-item Guid
        // rather than being left blank.
        var workItem = BuildWorkItem(applicationReference: null);
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        Assert.NotNull(captured);
        Assert.Equal(workItem.Id.ToString(), captured!["reference"]);

        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "SubmissionConfirmation",
                "op@example.com",
                Arg.Any<Dictionary<string, string>>(),
                workItem.Id.ToString(),
                Arg.Any<string?>(),
                ct
            );

        Assert.NotNull(auditDetails);
        Assert.Equal(workItem.Id.ToString(), auditDetails!["reference"]);
    }

    [Fact]
    public async Task OnSubmittedAsync_uses_application_reference_in_skipped_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string?>? auditDetails = null;
        auditAppender
            .AppendAsync(
                Arg.Any<Guid>(),
                "notification-skipped",
                Arg.Any<string>(),
                // RA-240 means OnSubmittedAsync can record a second
                // notification-skipped entry (the regulator send, keyed on the
                // work-item id). Capture only the operator-facing one so this
                // assertion stays pinned to the SubmissionConfirmation reference.
                Arg.Do<Dictionary<string, string?>>(d =>
                {
                    if (d["templateKey"] == "SubmissionConfirmation")
                    {
                        auditDetails = d;
                    }
                }),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);

        // Missing operator email takes the skip path; payload is still present
        // so the reference resolves from applicationReference.
        var workItem = BuildWorkItem(operatorEmail: null);
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        Assert.NotNull(auditDetails);
        Assert.Equal(ApplicationReference, auditDetails!["reference"]);
    }

    [Fact]
    public async Task OnSubmittedAsync_falls_back_to_work_item_id_in_skipped_audit_when_reference_absent()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string?>? auditDetails = null;
        auditAppender
            .AppendAsync(
                Arg.Any<Guid>(),
                "notification-skipped",
                Arg.Any<string>(),
                // See above: scope the capture to the operator-facing skip.
                Arg.Do<Dictionary<string, string?>>(d =>
                {
                    if (d["templateKey"] == "SubmissionConfirmation")
                    {
                        auditDetails = d;
                    }
                }),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);

        // Legacy item on the skip path: no operator email (skip) AND no
        // applicationReference, so the skipped-audit reference falls back to
        // the work-item Guid rather than being left blank.
        var workItem = BuildWorkItem(operatorEmail: null, applicationReference: null);
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        Assert.NotNull(auditDetails);
        Assert.Equal(workItem.Id.ToString(), auditDetails!["reference"]);
    }
}
