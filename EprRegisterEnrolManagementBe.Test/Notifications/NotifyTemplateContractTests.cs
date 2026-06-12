using System.Security.Claims;
using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.Notifications;

/// <summary>
/// RA-201: drives <see cref="ReAccreditationNotificationHook"/> for every
/// re-accreditation lifecycle event, captures the personalisation it hands to
/// <c>INotifyClient</c>, and asserts the captured keys SATISFY (are a superset
/// of) the required placeholders declared in <see cref="NotifyTemplateContract"/>.
///
/// This is the regression guard that would have failed on the missing
/// <c>sla_deadline</c> placeholder for the SlaExtended template. It does not
/// touch the network — see <see cref="NotifyLiveTemplateContractTests"/> for
/// the opt-in check against real Notify template bodies.
/// </summary>
public class NotifyTemplateContractTests
{
    private static readonly ClaimsPrincipal s_user = new(new ClaimsIdentity(
    [
        new Claim("user:id", "user-1"),
        new Claim("user:name", "Alice")
    ], "test"));

    /// <summary>
    /// Each lifecycle event: the action id (null = submission), the template
    /// key it maps to, and whether it needs an SLA clock stamped on the item.
    /// </summary>
    public static TheoryData<string?, string, bool> LifecycleEvents() => new()
    {
        { null, "SubmissionConfirmation", false },
        { "duly-make", "DulyMade", false },
        { "payment-received", "AssessmentInProgress", false },
        { "sla-extend", "SlaExtended", true },
        { "approve", "Decision", false },
        { "reject", "Decision", false },
    };

    [Theory]
    [MemberData(nameof(LifecycleEvents))]
    public async Task hook_personalisation_satisfies_template_contract(
        string? actionId, string templateKey, bool needsSlaClock)
    {
        var ct = TestContext.Current.CancellationToken;
        var notifyClient = Substitute.For<INotifyClient>();
        var auditAppender = Substitute.For<IWorkItemAuditAppender>();
        Dictionary<string, string>? captured = null;
        notifyClient.SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Do<Dictionary<string, string>>(d => captured = d),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(NotifySendResult.Success("msg"));

        var workItem = BuildRepresentativeWorkItem(needsSlaClock);
        var sut = new ReAccreditationNotificationHook(
            notifyClient, auditAppender,
            NullLogger<ReAccreditationNotificationHook>.Instance);

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
        var missing = required
            .Where(key => !captured!.ContainsKey(key))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"Template '{templateKey}' (action '{actionId ?? "submit"}') is missing required " +
            $"personalisation placeholder(s): {string.Join(", ", missing)}. " +
            $"Supplied keys: {string.Join(", ", captured!.Keys.OrderBy(k => k))}.");

        // No required placeholder may be sent as null/empty — Notify treats an
        // empty value differently from a present one and operator copy would
        // render blank.
        foreach (var key in required)
        {
            Assert.False(
                string.IsNullOrEmpty(captured![key]),
                $"Required placeholder '{key}' for template '{templateKey}' was empty.");
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
            SlaClock = needsSlaClock
                ? new WorkItemSlaClock
                {
                    StartedAt = new DateTime(2025, 10, 9, 0, 0, 0, DateTimeKind.Utc),
                    TargetDuration = TimeSpan.FromDays(84)
                }
                : null,
            TemplateSnapshot = WorkItemTemplateSnapshot.Capture(new ReAccreditationType()),
            TemplateVersion = "v3"
        };
    }
}
