using System.Security.Claims;
using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.Notifications;

/// <summary>
/// RA-201: drives the re-accreditation notification hooks for every lifecycle
/// event, captures the personalisation handed to <c>INotifyClient</c>, and
/// asserts the captured keys SATISFY (are a superset of) the required
/// placeholders declared in <see cref="NotifyTemplateContract"/>.
///
/// RA-316: every lifecycle event now goes through
/// <see cref="ReAccreditationNotificationHook"/>, DulyMade included. It
/// previously had a second sender — an auto-transition hook with its own copy of
/// the send logic — which needed a separate contract test; folding it into the
/// table below is what removed that duplication.
/// </summary>
public class NotifyTemplateContractTests
{
    private static readonly ClaimsPrincipal s_user = new(
        new ClaimsIdentity(
            [new Claim("user:id", "user-1"), new Claim("user:name", "Alice")],
            "test"
        )
    );

    /// <summary>
    /// Each lifecycle event handled by <c>ReAccreditationNotificationHook</c>:
    /// the action id (null = submission), the template key it maps to, and
    /// whether it needs an SLA clock stamped on the item.
    /// RA-316: duly-make is now in this table rather than tested separately.
    /// It used to be sent by the deleted <c>ReAccreditationDulyMadeHook</c>,
    /// which hand-rolled its own copy of the send logic and so needed its own
    /// contract test; it now routes through the same generic hook path as every
    /// other lifecycle event, which is the point of the change.
    /// RA-211: reject is deliberately absent — it no longer sends any
    /// notification (see ReAccreditationNotificationHookTests.
    /// OnActionAppliedAsync_reject_does_not_call_notify_client).
    /// RA-581: payment-received and sla-extend are also absent — these two
    /// emails were removed and no longer send any notification (see
    /// ReAccreditationNotificationHookTests.
    /// OnActionAppliedAsync_payment_received_does_not_call_notify_client /
    /// OnActionAppliedAsync_sla_extend_does_not_call_notify_client).
    /// </summary>
    public static TheoryData<string?, string, bool> LifecycleEvents() =>
        new()
        {
            { null, "SubmissionConfirmation", false },
            { "duly-make", "DulyMade", true },
            { "query-during-assessment", "Queried", false },
            { "approve", "Decision", false },
        };

    [Theory]
    [MemberData(nameof(LifecycleEvents))]
    public async Task hook_personalisation_satisfies_template_contract(
        string? actionId,
        string templateKey,
        bool needsSlaClock
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        // RA-581: SubmissionConfirmation's OnSubmittedAsync also fires the
        // regulator-facing OperatorApplicationSubmission send now that the
        // resolver below returns a real mailbox (needed for regulatorEmail on
        // the other rows). Capture only the call whose OWN templateKey
        // matches the row under test, rather than "whichever call happened
        // last", so the regulator send can't clobber `captured`.
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(callInfo =>
            {
                if (
                    string.Equals(
                        callInfo.ArgAt<string>(0),
                        templateKey,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    captured = callInfo.ArgAt<Dictionary<string, string>>(2);
                }
                return NotifySendResult.Success("msg");
            });

        var workItem = BuildRepresentativeWorkItem(needsSlaClock);
        // RA-581: the resolver now drives BOTH the RA-240 regulator-facing
        // send (which this test wants skipped, so `captured` stays pinned to
        // the operator-facing template under test) AND the regulatorEmail
        // value read into operator-facing personalisation — those need to
        // stay in sync, so a null return here would make regulatorEmail
        // empty and fail the "required placeholders are non-empty" check
        // below for England, the nation BuildRepresentativeWorkItem stamps.
        var regulatorMailboxResolver = Substitute.For<IRegulatorMailboxResolver>();
        regulatorMailboxResolver.Resolve(Nation.England).Returns("packagingnotifications@environment-agency.gov.uk");
        var persistence = Substitute.For<IWorkItemPersistence>();
        // This contract asserts required placeholders are non-empty — i.e. it
        // describes a correctly-configured environment.
        var sut = new ReAccreditationNotificationHook(
            notifyClient,
            auditAppender,
            regulatorMailboxResolver,
            persistence,
            NullLogger<ReAccreditationNotificationHook>.Instance,
            Options.Create(new NotifyConfig
            {
                RegulatorNames = { ["England"] = "Environment Agency (EA)" }
            })
        );

        if (actionId is null)
        {
            await sut.OnSubmittedAsync(workItem, s_user, ct);
        }
        else
        {
            await sut.OnActionAppliedAsync(workItem, actionId, "any-from-state", s_user, ct);
        }

        Assert.NotNull(captured);

        var required = NotifyTemplateContract.RequiredPlaceholders[templateKey];
        var missing = required.Where(key => !captured!.ContainsKey(key)).ToList();

        Assert.True(
            missing.Count == 0,
            $"Template '{templateKey}' (action '{actionId ?? "submit"}') is missing required "
                + $"personalisation placeholder(s): {string.Join(", ", missing)}. "
                + $"Supplied keys: {string.Join(", ", captured!.Keys.OrderBy(k => k))}."
        );

        // Notify also 400s on UNEXPECTED personalisation keys, so the captured
        // keys must be a subset of the template's full allowed set (required +
        // optional). A surplus key here would be silently accepted by the
        // superset check above but rejected live by Notify.
        var allowed = NotifyTemplateContract.AllowedPlaceholders[templateKey];
        var surplus = captured!.Keys.Where(key => !allowed.Contains(key)).ToList();

        Assert.True(
            surplus.Count == 0,
            $"Template '{templateKey}' (action '{actionId ?? "submit"}') supplies "
                + $"surplus personalisation placeholder(s) Notify would reject: "
                + $"{string.Join(", ", surplus)}. "
                + $"Allowed keys: {string.Join(", ", allowed.OrderBy(k => k))}."
        );

        foreach (var key in required)
        {
            Assert.False(
                string.IsNullOrEmpty(captured![key]),
                $"Required placeholder '{key}' for template '{templateKey}' was empty."
            );
        }
    }

    private static WorkItem BuildRepresentativeWorkItem(bool needsSlaClock)
    {
        var payload = new BsonDocument
        {
            ["organisationName"] = "Acme Recycling Ltd",
            ["registrationNumber"] = "EX-2024-001",
            ["operatorEmail"] = "operator@example.com",
            // RA-581: regulatorName/regulatorEmail both need a resolvable
            // nation; England is matched by the RegulatorNames entry and the
            // mailbox resolver the SUT is built with below.
            ["nation"] = "England",
            // RA-581: material/accreditationYear/siteAddress/submitterContactDetails
            // are needed by one or more of the five operator-facing templates'
            // required sets (material, year, SiteAddress, contactName).
            ["material"] = "plastic",
            ["accreditationYear"] = 2027,
            ["siteAddress"] = "1 Example Way, Anytown, EX4 1PL",
            ["submitterContactDetails"] = new BsonDocument { ["fullName"] = "Priya Patel" },
            // RA-291: the Queried template requires a non-empty Querieddate,
            // read from the current query the query service stamps on the
            // payload (RaisedAt). Supply one so the queried contract row
            // exercises the non-empty path.
            ["currentQuery"] = new BsonDocument
            {
                ["reason"] = "Please confirm the tonnage figures.",
                ["sections"] = new BsonArray { "prn-tonnage" },
                ["raisedAt"] = new DateTime(2025, 11, 3, 0, 0, 0, DateTimeKind.Utc),
            },
        };

        return new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            Payload = payload,
            // RA-581: SubmissionConfirmation's date and DulyMade's
            // SubmissionDate both require a non-empty formatted value.
            SubmittedAt = new DateTime(2025, 10, 1, 9, 0, 0, DateTimeKind.Utc),
            SlaClock = needsSlaClock
                ? new WorkItemSlaClock
                {
                    StartedAt = new DateTime(2025, 10, 9, 0, 0, 0, DateTimeKind.Utc),
                    TargetDuration = TimeSpan.FromDays(84),
                }
                : null,
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType()),
            TemplateVersion = "v3",
        };
    }
}
