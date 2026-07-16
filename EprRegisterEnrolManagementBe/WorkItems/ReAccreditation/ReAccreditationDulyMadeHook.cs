using System.Security.Claims;
using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.Logging;
using MongoDB.Bson.Serialization;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Automatically transitions a re-accreditation work item from
/// <c>submitted</c> to <c>duly-made</c> when all submitted-state tasks are
/// completed, starts the SLA clock, and sends the DulyMade operator
/// notification.
///
/// Replaces the explicit "Mark as duly made" action button: the assessor no
/// longer needs to click a separate control after ticking the last task.
///
/// The SLA clock started here tracks the time since the application was
/// marked duly made. It is reset (restarted from payment date) when the
/// <c>payment-received</c> transition is applied, at which point it tracks
/// the assessment period instead.
///
/// Persistence failures propagate so the originating task-completion
/// request returns 500 rather than silently leaving the work item stuck in
/// <c>submitted</c> with no UI affordance to advance it.
/// </summary>
internal sealed class ReAccreditationDulyMadeHook(
    IWorkItemPersistence persistence,
    INotifyClient notifyClient,
    IWorkItemAuditAppender auditAppender,
    TimeProvider timeProvider,
    ILogger<ReAccreditationDulyMadeHook> logger
) : IWorkItemPostTaskHook
{
    public async Task OnAllTasksCompletedAsync(
        WorkItem workItem,
        string stateId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (
            !string.Equals(
                workItem.TypeId,
                ReAccreditationType.Id,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return;
        }

        if (!string.Equals(stateId, "submitted", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var fromStateId = workItem.StateId;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        workItem.StateId = "duly-made";
        workItem.LastModifiedAt = now;
        workItem.SlaClock = new WorkItemSlaClock { StartedAt = now };
        workItem.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = "action-applied",
                ActionDisplayName = "Action applied",
                Details = new Dictionary<string, string?>
                {
                    ["actionId"] = "duly-make",
                    ["actionDisplayName"] = "Mark as duly made",
                    ["fromStateId"] = fromStateId,
                    ["toStateId"] = workItem.StateId,
                },
                CreatedAt = now,
                CreatedBy = user.FindFirstValue("user:id"),
                CreatedByName = user.FindFirstValue("user:name"),
            }
        );
        workItem.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = "sla-clock-started",
                ActionDisplayName = "SLA clock started",
                Details = new Dictionary<string, string?>
                {
                    ["startedAt"] = now.ToString("O"),
                    ["targetDays"] = new WorkItemSlaClock().TargetDuration.TotalDays.ToString(),
                },
                CreatedAt = now,
                CreatedBy = user.FindFirstValue("user:id"),
                CreatedByName = user.FindFirstValue("user:name"),
            }
        );

        await persistence.ReplaceAsync(workItem, cancellationToken);

        logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) auto-transitioned submitted→duly-made "
                + "after all submitted-state tasks were completed.",
            workItem.Id,
            workItem.TypeId
        );

        await SendDulyMadeNotificationAsync(workItem, user, cancellationToken);
    }

    private async Task SendDulyMadeNotificationAsync(
        WorkItem workItem,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        ReAccreditationPayload? payload;
        try
        {
            payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to deserialise payload for work item {WorkItemId}; DulyMade notification skipped.",
                workItem.Id
            );
            return;
        }

        var recipient = payload?.OperatorEmail;
        // RA-248: surface the human-facing application reference (RA-#########)
        // in the ((reference)) placeholder, falling back to the internal
        // work-item Guid only for legacy/malformed items missing it.
        var reference = string.IsNullOrWhiteSpace(payload?.ApplicationReference)
            ? workItem.Id.ToString()
            : payload.ApplicationReference;

        if (string.IsNullOrWhiteSpace(recipient))
        {
            logger.LogInformation(
                "Skipping DulyMade notification for work item {WorkItemId}: no operator email.",
                workItem.Id
            );
            await auditAppender.AppendAsync(
                workItem.Id,
                "notification-skipped",
                "Application marked duly made email skipped",
                new Dictionary<string, string?>
                {
                    ["templateKey"] = "DulyMade",
                    ["reference"] = reference,
                    ["reason"] = "missing-operator-email",
                },
                user,
                cancellationToken
            );
            return;
        }

        var personalisation = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["organisation_name"] = payload!.OrganisationName ?? string.Empty,
            ["registration_number"] = payload.RegistrationNumber ?? string.Empty,
            ["reference"] = reference,
        };

        logger.LogInformation(
            "Sending DulyMade notification for work item {WorkItemId} (reference={Reference})",
            workItem.Id,
            reference
        );

        // RA-211: region drives the reply-to mailbox (NotifyConfig.GetReplyToId);
        // a missing/unresolvable Nation falls back to NotifyConfig.DefaultReplyToId.
        var region = payload.Nation?.ToString();

        var result = await notifyClient.SendEmailAsync(
            "DulyMade",
            recipient,
            personalisation,
            reference,
            region,
            cancellationToken
        );

        var details = new Dictionary<string, string?>
        {
            ["templateKey"] = "DulyMade",
            ["recipient"] = recipient,
            ["reference"] = reference,
            ["providerMessageId"] = result.ProviderMessageId,
        };

        if (result.IsSuccess)
        {
            await auditAppender.AppendAsync(
                workItem.Id,
                "notification-sent",
                "Application marked duly made email sent",
                details,
                user,
                cancellationToken
            );
        }
        else
        {
            details["errorMessage"] = result.ErrorMessage;
            await auditAppender.AppendAsync(
                workItem.Id,
                "notification-failed",
                "Application marked duly made email failed",
                details,
                user,
                cancellationToken
            );
        }
    }
}
