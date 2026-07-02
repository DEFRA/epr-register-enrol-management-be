using System.Security.Claims;
using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
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
/// The DulyMade notification is sent by <see cref="ReAccreditationDulyMadeHook"/>
/// (auto-transition on task completion) rather than
/// <see cref="ReAccreditationNotificationHook"/> (explicit action), so
/// <see cref="DulyMade_personalisation_satisfies_template_contract"/> drives
/// it through that hook separately.
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
    /// Note: duly-make is handled by <c>ReAccreditationDulyMadeHook</c> and
    /// tested separately in <see cref="DulyMade_personalisation_satisfies_template_contract"/>.
    /// RA-211: reject is deliberately absent — it no longer sends any
    /// notification (see ReAccreditationNotificationHookTests.
    /// OnActionAppliedAsync_reject_does_not_call_notify_client).
    /// </summary>
    public static TheoryData<string?, string, bool> LifecycleEvents() =>
        new()
        {
            { null, "SubmissionConfirmation", false },
            { "payment-received", "AssessmentInProgress", false },
            { "sla-extend", "SlaExtended", true },
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
        notifyClient
            .SendEmailAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(),
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildRepresentativeWorkItem(needsSlaClock);
        var sut = new ReAccreditationNotificationHook(
            notifyClient,
            auditAppender,
            NullLogger<ReAccreditationNotificationHook>.Instance
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

    [Fact]
    public async Task DulyMade_personalisation_satisfies_template_contract()
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
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildRepresentativeWorkItem(false);
        var sut = new ReAccreditationDulyMadeHook(
            persistence,
            notifyClient,
            auditAppender,
            new FakeTimeProvider(),
            NullLogger<ReAccreditationDulyMadeHook>.Instance
        );

        await sut.OnAllTasksCompletedAsync(workItem, "submitted", s_user, ct);

        Assert.NotNull(captured);

        const string templateKey = "DulyMade";
        var required = NotifyTemplateContract.RequiredPlaceholders[templateKey];
        var missing = required.Where(key => !captured!.ContainsKey(key)).ToList();

        Assert.True(
            missing.Count == 0,
            $"Template '{templateKey}' (duly-make auto-transition) is missing required "
                + $"personalisation placeholder(s): {string.Join(", ", missing)}. "
                + $"Supplied keys: {string.Join(", ", captured!.Keys.OrderBy(k => k))}."
        );

        var allowed = NotifyTemplateContract.AllowedPlaceholders[templateKey];
        var surplus = captured!.Keys.Where(key => !allowed.Contains(key)).ToList();

        Assert.True(
            surplus.Count == 0,
            $"Template '{templateKey}' (duly-make auto-transition) supplies "
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
        };

        return new WorkItem
        {
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            Payload = payload,
            // RA-203: the Decision template requires a non-empty decision_notes
            // placeholder, sourced from the latest work-item-level note. Supply
            // one here so the approve/reject contract rows exercise the
            // non-empty path and the "required placeholder may not be empty"
            // assertion holds for the Decision template.
            Notes =
            [
                new WorkItemNote
                {
                    Text = "Decision rationale recorded by the assessor.",
                    CreatedAt = new DateTime(2025, 10, 9, 9, 30, 0, DateTimeKind.Utc),
                    TaskId = null,
                },
            ],
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
