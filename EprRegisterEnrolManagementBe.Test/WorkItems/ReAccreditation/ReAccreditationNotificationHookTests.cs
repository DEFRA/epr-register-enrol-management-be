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
        IEnumerable<WorkItemNote>? notes = null,
        bool nullNotes = false,
        Nation? nation = null,
        bool includeNation = false,
        string? assignedToName = null,
        string? assignedBy = null,
        string? applicationReference = ApplicationReference,
        DateTime? submittedAt = null
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
            Notes = nullNotes ? null! : (notes?.ToList() ?? new List<WorkItemNote>()),
            AssignedToName = assignedToName,
            AssignedBy = assignedBy,
            SubmittedAt = submittedAt ?? default,
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
        NotifyConfig? notifyConfig = null,
        string? caseManagementBaseUrl = null
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

        // RA-581: null models an environment with no case-management options
        // registered at all — distinct from a set-but-empty BaseUrl, and both
        // must degrade to an empty work_item_link rather than throwing.
        var caseManagementOptions = caseManagementBaseUrl is null
            ? null
            : Options.Create(new CaseManagementConfig { BaseUrl = caseManagementBaseUrl });

        return new(
            notifyClient,
            auditAppender,
            resolver,
            persistence,
            NullLogger<ReAccreditationNotificationHook>.Instance,
            Options.Create(notifyConfig ?? new NotifyConfig()),
            caseManagementOptions
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
    public async Task OnSubmittedAsync_populates_SubmissionConfirmation_fields_from_payload()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient
            .SendEmailAsync(
                "SubmissionConfirmation",
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildWorkItem(
            submittedAt: new DateTime(2025, 10, 9, 9, 0, 0, DateTimeKind.Utc)
        );
        workItem.Payload!["material"] = "plastic";
        workItem.Payload["accreditationYear"] = 2027;
        workItem.Payload["siteAddress"] = "1 Example Way, Anytown, EX4 1PL";
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        Assert.NotNull(captured);
        Assert.Equal("2027", captured!["year"]);
        Assert.Equal("plastic", captured["material"]);
        // AC05: no SiteName capture exists for the primary application.
        Assert.Equal(string.Empty, captured["SiteName"]);
        Assert.Equal("1 Example Way, Anytown, EX4 1PL", captured["SiteAddress"]);
        Assert.Equal("9 October 2025", captured["date"]);
    }

    [Fact]
    public async Task OnActionAppliedAsync_populates_DulyMade_SubmissionDate_and_regulatorEmail()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient
            .SendEmailAsync(
                "DulyMade",
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildWorkItem(
            nation: Nation.England,
            submittedAt: new DateTime(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc)
        );
        var resolver = ResolverReturning("packagingnotifications@environment-agency.gov.uk");
        var sut = BuildSut(notifyClient, auditAppender, resolver);

        await sut.OnActionAppliedAsync(workItem, "duly-make", fromStateId: "submitted", s_user, ct);

        Assert.NotNull(captured);
        Assert.Equal("1 September 2025", captured!["SubmissionDate"]);
        Assert.Equal("packagingnotifications@environment-agency.gov.uk", captured["regulatorEmail"]);
    }

    [Fact]
    public async Task OnActionAppliedAsync_populates_Queried_date_from_current_query_raised_at()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient
            .SendEmailAsync(
                "Queried",
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-queried"));

        var workItem = BuildWorkItem(stateId: "queried");
        workItem.Payload!["currentQuery"] = new BsonDocument
        {
            ["reason"] = "Please confirm the tonnage figures.",
            ["raisedAt"] = new DateTime(2025, 11, 3, 0, 0, 0, DateTimeKind.Utc),
        };
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(
            workItem,
            "query-during-duly-making",
            fromStateId: "submitted",
            s_user,
            ct
        );

        Assert.NotNull(captured);
        Assert.Equal("3 November 2025", captured!["Querieddate"]);
    }

    [Fact]
    public async Task OnActionAppliedAsync_sets_empty_Querieddate_when_current_query_absent()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient
            .SendEmailAsync(
                "Queried",
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg-queried"));

        // No currentQuery stamped at all — legacy item or a queried
        // transition applied outside ReAccreditationQueryService.
        var workItem = BuildWorkItem(stateId: "queried");
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(
            workItem,
            "query-during-duly-making",
            fromStateId: "submitted",
            s_user,
            ct
        );

        Assert.NotNull(captured);
        Assert.True(captured!.ContainsKey("Querieddate"));
        Assert.Equal(string.Empty, captured["Querieddate"]);
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

    // RA-581: payment-received and sla-extend were removed from
    // s_actionTemplates — these two emails are no longer required. Mirrors
    // OnActionAppliedAsync_reject_does_not_call_notify_client: the action's
    // own audit entry (and, for sla-extend, SlaService's own "sla-extended"
    // entry) is unaffected; this hook simply produces no Notify call or
    // notification audit entry for either action.
    [Theory]
    [InlineData("payment-received")]
    [InlineData("sla-extend")]
    public async Task OnActionAppliedAsync_removed_templates_do_not_call_notify_client(string actionId)
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();

        var workItem = BuildWorkItem();
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, actionId, fromStateId: "assessment-in-progress", s_user, ct);

        await notifyClient
            .DidNotReceiveWithAnyArgs()
            .SendEmailAsync(default!, default!, default!, default!, default!, ct);
        await auditAppender
            .DidNotReceiveWithAnyArgs()
            .AppendAsync(default, default!, default!, default!, default!, ct);
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
            "duly-make",
            fromStateId: "submitted",
            s_user,
            ct
        );

        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "DulyMade",
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
            "duly-make",
            fromStateId: "submitted",
            s_user,
            ct
        );

        // No nation on the payload: region falls through as null so
        // GovukNotifyClient's NotifyConfig.DefaultReplyToId fallback applies
        // rather than a bogus/empty region string.
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

    // ─── RA-291 (AC06): operator-service link in the Queried email ───

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

    [Fact]
    public async Task OnActionAppliedAsync_sends_Decision_template_on_approve()
    {
        // RA-581: the Decision template was simplified to a generic "a
        // decision has been made, check the service" message — it no longer
        // distinguishes approved/refused in its personalisation at all (see
        // NotifyTemplateContract.RequiredPlaceholders["Decision"]).
        // RA-211: reject deliberately sends no notification at all — see
        // OnActionAppliedAsync_reject_does_not_call_notify_client below.
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
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildWorkItem(stateId: "approved");
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(
            workItem,
            "approve",
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

    [Fact]
    public async Task OnActionAppliedAsync_skips_non_re_accreditation_work_items()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();

        var workItem = new WorkItem
        {
            TypeId = "some-other-type",
            StateId = "submitted",
            Payload = new BsonDocument { ["operatorEmail"] = "op@example.com" },
        };
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnActionAppliedAsync(workItem, "approve", fromStateId: "submitted", s_user, ct);

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

    // ─────── RA-581 (AC03/AC06): contact_name / regulator_name ───────

    [Fact]
    public async Task OnSubmittedAsync_uses_submitter_full_name_as_contact_name()
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

        var workItem = BuildWorkItem();
        workItem.Payload!["submitterContactDetails"] = new BsonDocument
        {
            ["fullName"] = "Barton Deckow",
        };
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        Assert.NotNull(captured);
        Assert.Equal("Barton Deckow", captured!["contactName"]);
    }

    // AC06: the organisation name becomes the contact_name placeholder when
    // the submitter's contact name is blank — covers both "field present but
    // blank" and "no submitterContactDetails at all" (pre-RA-480 items).
    [Theory]
    [InlineData(false, "")]
    [InlineData(false, "   ")]
    [InlineData(true, null)]
    public async Task OnSubmittedAsync_falls_back_to_organisation_name_for_contact_name_when_blank(
        bool omitSubmitterContactDetails,
        string? blankFullName
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

        var workItem = BuildWorkItem();
        if (!omitSubmitterContactDetails)
        {
            workItem.Payload!["submitterContactDetails"] = new BsonDocument
            {
                ["fullName"] = blankFullName,
            };
        }
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        Assert.NotNull(captured);
        Assert.Equal("Acme Ltd", captured!["contactName"]);
    }

    [Fact]
    public async Task OnSubmittedAsync_resolves_regulator_name_from_nation()
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
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildWorkItem(nation: Nation.Wales);
        var notifyConfig = new NotifyConfig();
        notifyConfig.RegulatorNames["Wales"] = "Natural Resources Wales (NRW)";
        var sut = BuildSut(notifyClient, auditAppender, notifyConfig: notifyConfig);

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        Assert.NotNull(captured);
        Assert.Equal("Natural Resources Wales (NRW)", captured!["regulatorName"]);
    }

    [Fact]
    public async Task OnSubmittedAsync_sets_empty_regulator_name_when_nation_unconfigured()
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
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg"));

        // No nation on the payload, and an empty RegulatorNames map — both
        // degrade to an empty string rather than throwing or leaving the key
        // absent (Notify 400s on a missing key, not an empty one).
        var workItem = BuildWorkItem();
        var sut = BuildSut(notifyClient, auditAppender);

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        Assert.NotNull(captured);
        Assert.True(captured!.ContainsKey("regulatorName"));
        Assert.Equal(string.Empty, captured["regulatorName"]);
    }

    // ─────── RA-204: Withdrawn notification ───────

    [Theory]
    [InlineData("withdraw")]
    [InlineData("withdraw-during-duly-made")]
    [InlineData("withdraw-during-assessment")]
    [InlineData("withdraw-during-decision")]
    [InlineData("withdraw-during-query")]
    // RA-252 (v10): withdrawal from 'updated' — added to the state machine
    // but missing here (and so silently sending no email at all) until
    // found via local testing (RA-581).
    [InlineData("withdraw-during-updated")]
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
    public async Task OnActionAppliedAsync_uses_latest_work_item_level_note_as_withdrawal_reason()
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
        Assert.Equal("Latest reason", captured!["withdrawal_reason"]);
        // RA-581: Withdrawn is the thinnest of the five operator templates —
        // no organisation_name, year, or material — but its other three
        // required keys are present.
        Assert.Equal("Acme Ltd", captured["contactName"]);
        Assert.Equal("EX-001", captured["registration_number"]);
        Assert.Equal(ApplicationReference, captured["reference"]);
        Assert.DoesNotContain("organisation_name", captured.Keys);
    }

    [Fact]
    public async Task OnActionAppliedAsync_sets_empty_withdrawal_reason_when_no_notes()
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
        Assert.True(captured!.ContainsKey("withdrawal_reason"));
        Assert.Equal(string.Empty, captured["withdrawal_reason"]);
    }

    [Fact]
    public async Task OnActionAppliedAsync_sets_empty_withdrawal_reason_when_notes_collection_is_null()
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
        Assert.True(captured!.ContainsKey("withdrawal_reason"));
        Assert.Equal(string.Empty, captured["withdrawal_reason"]);
    }

    // ─────── RA-581 (AC02): per-template trigger toggle ───────

    [Fact]
    public async Task OnSubmittedAsync_skips_operator_email_when_trigger_disabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();

        var workItem = BuildWorkItem();
        var notifyConfig = new NotifyConfig();
        notifyConfig.TriggersEnabled["SubmissionConfirmation"] = false;
        var sut = BuildSut(notifyClient, auditAppender, notifyConfig: notifyConfig);

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        await notifyClient
            .DidNotReceive()
            .SendEmailAsync(
                "SubmissionConfirmation",
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                cancellationToken: ct
            );
        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-skipped",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["templateKey"] == "SubmissionConfirmation"
                    && d["reason"] == "trigger-disabled"
                ),
                s_user,
                ct
            );
    }

    [Fact]
    public async Task OnActionAppliedAsync_skips_regulator_email_when_trigger_disabled()
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
            .Returns(NotifySendResult.Success("op-withdrawn-msg"));

        var workItem = BuildWorkItem(
            stateId: "withdrawn",
            includeNation: true,
            nation: Nation.England
        );
        var notifyConfig = new NotifyConfig();
        notifyConfig.TriggersEnabled["ApplicationWithdrawn"] = false;
        var sut = BuildSut(
            notifyClient,
            auditAppender,
            ResolverReturning("regulator@england.example.gov.uk"),
            persistedWorkItem: workItem,
            notifyConfig: notifyConfig
        );

        await sut.OnActionAppliedAsync(workItem, "withdraw", fromStateId: "submitted", s_user, ct);

        // Operator email is unaffected — only the ApplicationWithdrawn trigger
        // was switched off.
        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "Withdrawn",
                "op@example.com",
                Arg.Any<Dictionary<string, string>>(),
                ApplicationReference,
                Arg.Any<string?>(),
                ct
            );
        await notifyClient
            .DidNotReceive()
            .SendEmailAsync(
                "ApplicationWithdrawn",
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
                    d["templateKey"] == "ApplicationWithdrawn"
                    && d["reason"] == "trigger-disabled"
                ),
                s_user,
                ct
            );
    }

    [Fact]
    public async Task OnActionAppliedAsync_sends_when_trigger_map_has_no_entry_for_the_template()
    {
        // RA-581: an absent key means enabled — the map is opt-out, not an
        // opt-in allow-list — so a template with no configured entry still
        // sends.
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
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildWorkItem(stateId: "withdrawn");
        var notifyConfig = new NotifyConfig();
        notifyConfig.TriggersEnabled["SomeOtherTemplate"] = false;
        var sut = BuildSut(notifyClient, auditAppender, notifyConfig: notifyConfig);

        await sut.OnActionAppliedAsync(workItem, "withdraw", fromStateId: "submitted", s_user, ct);

        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "Withdrawn",
                "op@example.com",
                Arg.Any<Dictionary<string, string>>(),
                ApplicationReference,
                cancellationToken: ct
            );
    }

    // ─────── RA-240: OperatorApplicationSubmission notification ───────

    [Fact]
    public async Task OnSubmittedAsync_sends_OperatorApplicationSubmission_to_resolved_mailbox_and_records_sent_audit_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? regulatorPersonalisation = null;
        notifyClient
            .SendEmailAsync(
                "OperatorApplicationSubmission",
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

        // Both operator confirmation and regulator submission were sent, both
        // carrying the RA-248 human-facing application reference (RA-581:
        // every regulator-facing template now surfaces ((reference)) too).
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
                "OperatorApplicationSubmission",
                "regulator@england.example.gov.uk",
                Arg.Any<Dictionary<string, string>>(),
                ApplicationReference,
                Arg.Any<string?>(),
                ct
            );

        Assert.NotNull(regulatorPersonalisation);
        Assert.Equal("Acme Ltd", regulatorPersonalisation!["organisation_name"]);
        Assert.Equal("EX-001", regulatorPersonalisation["registration_number"]);
        Assert.Equal(ApplicationReference, regulatorPersonalisation["reference"]);

        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-sent",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["templateKey"] == "OperatorApplicationSubmission"
                    && d["recipient"] == "regulator@england.example.gov.uk"
                    && d["nation"] == "England"
                    && d["providerMessageId"] == "reg-msg-1"
                ),
                s_user,
                ct
            );
    }

    [Fact]
    public async Task OnSubmittedAsync_skips_OperatorApplicationSubmission_when_nation_mailbox_unconfigured()
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
                "OperatorApplicationSubmission",
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
                    d["templateKey"] == "OperatorApplicationSubmission"
                    && d["reason"] == "missing-regulator-mailbox"
                    && d["nation"] == "Scotland"
                ),
                s_user,
                ct
            );
    }

    // ─────── RA-581: ApplicationWithdrawn regulator notification ───────

    [Fact]
    public async Task OnActionAppliedAsync_sends_ApplicationWithdrawn_to_resolved_mailbox_with_human_facing_reference()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? regulatorPersonalisation = null;
        notifyClient
            .SendEmailAsync(
                "ApplicationWithdrawn",
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => regulatorPersonalisation = d),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("reg-withdrawn-msg"));
        notifyClient
            .SendEmailAsync(
                "Withdrawn",
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("op-withdrawn-msg"));

        var workItem = BuildWorkItem(
            stateId: "withdrawn",
            includeNation: true,
            nation: Nation.England
        );
        var sut = BuildSut(
            notifyClient,
            auditAppender,
            ResolverReturning("regulator@england.example.gov.uk"),
            persistedWorkItem: workItem
        );

        await sut.OnActionAppliedAsync(workItem, "withdraw", fromStateId: "submitted", s_user, ct);

        // Operator send keeps the existing behaviour (Withdrawn template).
        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "Withdrawn",
                "op@example.com",
                Arg.Any<Dictionary<string, string>>(),
                ApplicationReference,
                Arg.Any<string?>(),
                ct
            );

        // Regulator send: ApplicationWithdrawn's Notify-tracking reference AND
        // its ((reference)) placeholder both carry the human-facing
        // RA-######### reference, not the internal work-item Guid (RA-581:
        // every regulator-facing template now does this).
        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "ApplicationWithdrawn",
                "regulator@england.example.gov.uk",
                Arg.Any<Dictionary<string, string>>(),
                ApplicationReference,
                Arg.Any<string?>(),
                ct
            );

        Assert.NotNull(regulatorPersonalisation);
        Assert.Equal("Acme Ltd", regulatorPersonalisation!["organisation_name"]);
        Assert.Equal("EX-001", regulatorPersonalisation["registration_number"]);
        Assert.Equal(ApplicationReference, regulatorPersonalisation["reference"]);

        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-sent",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["templateKey"] == "ApplicationWithdrawn"
                    && d["recipient"] == "regulator@england.example.gov.uk"
                    && d["reference"] == ApplicationReference
                    && d["providerMessageId"] == "reg-withdrawn-msg"
                ),
                s_user,
                ct
            );
    }

    [Fact]
    public async Task OnActionAppliedAsync_falls_back_to_work_item_id_for_ApplicationWithdrawn_reference_when_application_reference_missing()
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
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildWorkItem(
            stateId: "withdrawn",
            includeNation: true,
            nation: Nation.England,
            applicationReference: null
        );
        var sut = BuildSut(
            notifyClient,
            auditAppender,
            ResolverReturning("regulator@england.example.gov.uk"),
            persistedWorkItem: workItem
        );

        await sut.OnActionAppliedAsync(workItem, "withdraw", fromStateId: "submitted", s_user, ct);

        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "ApplicationWithdrawn",
                "regulator@england.example.gov.uk",
                Arg.Any<Dictionary<string, string>>(),
                workItem.Id.ToString(),
                Arg.Any<string?>(),
                ct
            );
    }

    [Fact]
    public async Task OnActionAppliedAsync_skips_ApplicationWithdrawn_when_nation_mailbox_unconfigured()
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
        var workItem = BuildWorkItem(
            stateId: "withdrawn",
            includeNation: true,
            nation: Nation.Scotland
        );
        var sut = BuildSut(
            notifyClient,
            auditAppender,
            ResolverReturning(null),
            persistedWorkItem: workItem
        );

        await sut.OnActionAppliedAsync(workItem, "withdraw", fromStateId: "submitted", s_user, ct);

        // Operator confirmation still sent; regulator withdrawal skipped.
        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "Withdrawn",
                "op@example.com",
                Arg.Any<Dictionary<string, string>>(),
                ApplicationReference,
                Arg.Any<string?>(),
                ct
            );
        await notifyClient
            .DidNotReceive()
            .SendEmailAsync(
                "ApplicationWithdrawn",
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
                    d["templateKey"] == "ApplicationWithdrawn"
                    && d["reason"] == "missing-regulator-mailbox"
                    && d["nation"] == "Scotland"
                ),
                s_user,
                ct
            );
    }

    [Fact]
    public async Task OnSubmittedAsync_logs_a_warning_when_the_skipped_audit_entry_fails_to_persist()
    {
        // Covers SendRegulatorEmailAsync's `if (!skipAppended)` branch: the
        // audit-append itself can fail (e.g. a concurrent mutation lost the
        // race), and the hook must swallow that rather than throwing —
        // the missing-mailbox skip is otherwise indistinguishable from the
        // happy skip path above.
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
        auditAppender
            .AppendAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string?>>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(false);

        var workItem = BuildWorkItem(includeNation: true, nation: Nation.Scotland);
        var sut = BuildSut(
            notifyClient,
            auditAppender,
            ResolverReturning(null),
            persistedWorkItem: workItem
        );

        // Must not throw despite the failed audit append.
        var exception = await Record.ExceptionAsync(
            () => sut.OnSubmittedAsync(workItem, s_user, ct));

        Assert.Null(exception);
    }

    [Fact]
    public async Task OnSubmittedAsync_skips_OperatorApplicationSubmission_when_nation_absent()
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
                "OperatorApplicationSubmission",
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
                    d["templateKey"] == "OperatorApplicationSubmission"
                    && d["reason"] == "missing-regulator-mailbox"
                    && d["nation"] == null
                ),
                s_user,
                ct
            );
    }

    [Fact]
    public async Task OnSubmittedAsync_records_failed_audit_entry_when_OperatorApplicationSubmission_send_fails()
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
                "OperatorApplicationSubmission",
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
                    d["templateKey"] == "OperatorApplicationSubmission"
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
                ApplicationReference,
                Arg.Any<string?>(),
                ct
            );

        Assert.NotNull(captured);
        Assert.Equal("Acme Ltd", captured!["organisation_name"]);
        Assert.Equal("EX-001", captured["registration_number"]);
        Assert.Equal(ApplicationReference, captured["reference"]);
        // RA-581: assignment_event is no longer sent — the live template
        // dropped it — but the description built from it (asserted via
        // actionDisplayName below) still distinguishes the three cases.
        Assert.DoesNotContain("assignment_event", captured.Keys);
        Assert.Equal(expectedOfficerName, captured["officer_name"]);
        // s_user's user:name claim — the acting principal, present on every
        // change including unassign (where AssignedBy has already been cleared).
        Assert.Equal("Alice", captured["changed_by"]);

        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-sent",
                Arg.Is<string>(desc => desc.Contains(expectedEvent)),
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

    // ─────── RA-581: QueryResponse notification (operator responds) ───────

    [Theory]
    [InlineData("resume-during-duly-making")]
    [InlineData("resume-during-duly-made")]
    [InlineData("resume-during-assessment")]
    [InlineData("resume-during-decision")]
    public async Task OnActionAppliedAsync_sends_QueryResponse_to_regulator_only(string actionId)
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient
            .SendEmailAsync(
                "QueryResponse",
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("query-response-msg"));

        var workItem = BuildWorkItem(
            stateId: "assessment-in-progress",
            includeNation: true,
            nation: Nation.England
        );
        var sut = BuildSut(
            notifyClient,
            auditAppender,
            ResolverReturning("regulator@england.example.gov.uk"),
            persistedWorkItem: workItem
        );

        await sut.OnActionAppliedAsync(workItem, actionId, fromStateId: "queried", s_user, ct);

        // Regulator-only: no operator-facing template maps to a
        // resume-during-* action.
        await notifyClient
            .Received(1)
            .SendEmailAsync(
                "QueryResponse",
                "regulator@england.example.gov.uk",
                Arg.Any<Dictionary<string, string>>(),
                ApplicationReference,
                Arg.Any<string?>(),
                ct
            );
        await notifyClient
            .DidNotReceive()
            .SendEmailAsync(
                Arg.Is<string>(key => key != "QueryResponse"),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                ct
            );

        Assert.NotNull(captured);
        Assert.Equal("Acme Ltd", captured!["organisation_name"]);
        Assert.Equal("EX-001", captured["registration_number"]);
        Assert.Equal(ApplicationReference, captured["reference"]);

        await auditAppender
            .Received(1)
            .AppendAsync(
                workItem.Id,
                "notification-sent",
                Arg.Any<string>(),
                Arg.Is<Dictionary<string, string?>>(d =>
                    d["templateKey"] == "QueryResponse"
                    && d["providerMessageId"] == "query-response-msg"
                ),
                s_user,
                ct
            );
    }

    [Fact]
    public async Task OnActionAppliedAsync_skips_QueryResponse_when_nation_mailbox_unconfigured()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();

        // Scotland is an unconfigured placeholder → resolver returns null.
        var workItem = BuildWorkItem(includeNation: true, nation: Nation.Scotland);
        var sut = BuildSut(
            notifyClient,
            auditAppender,
            ResolverReturning(null),
            persistedWorkItem: workItem
        );

        await sut.OnActionAppliedAsync(
            workItem,
            "resume-during-assessment",
            fromStateId: "queried",
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
                    d["templateKey"] == "QueryResponse"
                    && d["reason"] == "missing-regulator-mailbox"
                ),
                s_user,
                ct
            );
    }

    // ─────── RA-581: work_item_link (regulator-facing templates) ───────

    [Fact]
    public async Task SendRegulatorEmailAsync_builds_work_item_link_from_case_management_base_url()
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
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildWorkItem(includeNation: true, nation: Nation.England);
        var sut = BuildSut(
            notifyClient,
            auditAppender,
            ResolverReturning("regulator@england.example.gov.uk"),
            persistedWorkItem: workItem,
            // Trailing slash deliberately included: BuildWorkItemLink trims
            // it so the link never ends up with a double slash.
            caseManagementBaseUrl: "https://management.example.gov.uk/"
        );

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        Assert.NotNull(captured);
        Assert.Equal(
            $"https://management.example.gov.uk/work-items/{workItem.Id}",
            captured!["work_item_link"]
        );
    }

    [Fact]
    public async Task SendRegulatorEmailAsync_sets_empty_work_item_link_when_case_management_base_url_unconfigured()
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
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildWorkItem(includeNation: true, nation: Nation.England);
        var sut = BuildSut(
            notifyClient,
            auditAppender,
            ResolverReturning("regulator@england.example.gov.uk"),
            persistedWorkItem: workItem
        );

        await sut.OnSubmittedAsync(workItem, s_user, ct);

        Assert.NotNull(captured);
        Assert.True(captured!.ContainsKey("work_item_link"));
        Assert.Equal(string.Empty, captured["work_item_link"]);
    }

    // ─────── RA-581: Withdrawal_reason on the regulator-facing send ───────

    [Fact]
    public async Task OnActionAppliedAsync_includes_Withdrawal_reason_on_regulator_ApplicationWithdrawn_send()
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? regulatorPersonalisation = null;
        notifyClient
            .SendEmailAsync(
                "ApplicationWithdrawn",
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => regulatorPersonalisation = d),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("reg-msg"));
        notifyClient
            .SendEmailAsync(
                "Withdrawn",
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("op-msg"));

        var workItem = BuildWorkItem(
            stateId: "withdrawn",
            includeNation: true,
            nation: Nation.England,
            notes: [Note("Site closed permanently.", new DateTime(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc))]
        );
        var sut = BuildSut(
            notifyClient,
            auditAppender,
            ResolverReturning("regulator@england.example.gov.uk"),
            persistedWorkItem: workItem
        );

        await sut.OnActionAppliedAsync(workItem, "withdraw", fromStateId: "submitted", s_user, ct);

        Assert.NotNull(regulatorPersonalisation);
        Assert.Equal("Site closed permanently.", regulatorPersonalisation!["Withdrawal_reason"]);
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
