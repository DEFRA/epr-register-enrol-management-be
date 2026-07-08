using System.Security.Claims;
using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using MongoDB.Bson.Serialization;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Compound payment operation. Mutates the work item in-memory and writes
/// once (single <see cref="IWorkItemPersistence.ReplaceAsync"/>) so a
/// partial failure cannot leave the item with an SLA clock but no
/// transition, or a transition but no unassign audit entry.
///
/// Audit entries are attributed to the paying operator user
/// (<see cref="Models.PaymentCompletedRequest.PaidByUserId"/>) rather than
/// to the regulator calling the endpoint — the operator triggered the
/// state change by paying.
/// </summary>
internal sealed class ReAccreditationPaymentService(
    IWorkItemPersistence persistence,
    INotifyClient notifyClient,
    IWorkItemAuditAppender auditAppender,
    TimeProvider timeProvider,
    ILogger<ReAccreditationPaymentService> logger
) : IReAccreditationPaymentService
{
    private const string AssessmentInProgressState = "assessment-in-progress";
    private const string DulyMadeState = "duly-made";

    public async Task<WorkItemActionResult> RecordPaymentAsync(
        Guid workItemId,
        PaymentCompletedRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."
            );
        }

        if (
            !string.Equals(
                workItem.TypeId,
                ReAccreditationType.Id,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item {workItemId} is of type '{workItem.TypeId}', not '{ReAccreditationType.Id}'."
            );
        }

        if (!string.Equals(workItem.StateId, DulyMadeState, StringComparison.OrdinalIgnoreCase))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item {workItemId} is in state '{workItem.StateId}'; payment-completed requires '{DulyMadeState}'."
            );
        }

        // paidAt must be UTC. Unspecified-kind values (no Z / +00:00 suffix in JSON)
        // are rejected rather than silently re-labelled — they could be hours off.
        if (request.PaidAt.Kind != DateTimeKind.Utc)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                "'paidAt' must be UTC (use the Z or +00:00 suffix)."
            );
        }
        var paidAt = request.PaidAt;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (paidAt > now.AddMinutes(5))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                "'paidAt' must not be in the future."
            );
        }

        // Mutate in-memory before the single ReplaceAsync so any
        // persistence failure leaves the on-disk document untouched.
        var previousAssigneeId = workItem.AssignedToId;
        var previousAssigneeName = workItem.AssignedToName;

        workItem.SlaClock = new WorkItemSlaClock { StartedAt = paidAt };
        workItem.StateId = AssessmentInProgressState;
        workItem.AssignedToId = null;
        workItem.AssignedToName = null;
        workItem.AssignedAt = null;
        workItem.AssignedBy = null;
        workItem.LastModifiedAt = now;

        // Four audit entries attributed to the paying operator user.
        AppendPaymentAudit(
            workItem,
            "payment-completed",
            "Payment completed",
            request,
            paidAt,
            new()
            {
                ["amountPence"] = request.AmountPence.ToString(),
                ["reference"] = request.Reference,
            }
        );
        AppendPaymentAudit(
            workItem,
            "sla-clock-started",
            "SLA clock started",
            request,
            paidAt,
            new()
            {
                ["startedAt"] = paidAt.ToString("O"),
                ["targetDays"] = workItem.SlaClock!.TargetDuration.TotalDays.ToString(),
            }
        );
        if (previousAssigneeId is not null)
        {
            AppendPaymentAudit(
                workItem,
                "unassigned",
                "Unassigned on payment",
                request,
                now,
                new()
                {
                    ["previousAssigneeId"] = previousAssigneeId,
                    ["previousAssigneeName"] = previousAssigneeName,
                }
            );
        }
        AppendPaymentAudit(
            workItem,
            "state-changed",
            "State changed",
            request,
            now,
            new() { ["fromStateId"] = DulyMadeState, ["toStateId"] = AssessmentInProgressState }
        );

        try
        {
            await persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.ConcurrencyConflict,
                $"Work item '{workItemId}' was modified concurrently. Reload and retry."
            );
        }

        logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) payment completed by operator {PaidByUserId}; "
                + "SLA clock started at {PaidAt}; transitioned to {State}",
            workItem.Id,
            workItem.TypeId,
            request.PaidByUserId,
            paidAt,
            AssessmentInProgressState
        );

        await SendNotificationAsync(workItem, request, cancellationToken);

        return WorkItemActionResult.Success(workItem);
    }

    private static void AppendPaymentAudit(
        WorkItem workItem,
        string action,
        string displayName,
        PaymentCompletedRequest request,
        DateTime createdAt,
        Dictionary<string, string?> details
    )
    {
        workItem.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = action,
                ActionDisplayName = displayName,
                CreatedAt = createdAt,
                CreatedBy = request.PaidByUserId,
                CreatedByName = request.PaidByEmail,
                Details = details,
            }
        );
    }

    private async Task SendNotificationAsync(
        WorkItem workItem,
        PaymentCompletedRequest request,
        CancellationToken cancellationToken
    )
    {
        ReAccreditationPayload? payload = null;
        try
        {
            payload = BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to deserialise payload for work item {WorkItemId}; AssessmentInProgress email not sent.",
                workItem.Id
            );
        }

        // RA-248: surface the human-facing application reference (RA-#########)
        // in the ((reference)) placeholder, falling back to the internal
        // work-item Guid only for legacy/malformed items missing it.
        var reference = string.IsNullOrWhiteSpace(payload?.ApplicationReference)
            ? workItem.Id.ToString()
            : payload.ApplicationReference;

        var personalisation = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["organisation_name"] = payload?.OrganisationName ?? string.Empty,
            ["registration_number"] = payload?.RegistrationNumber ?? string.Empty,
            ["reference"] = reference,
        };

        // RA-211: region drives the reply-to mailbox (NotifyConfig.GetReplyToId);
        // a missing/unresolvable Nation falls back to NotifyConfig.DefaultReplyToId.
        var region = payload?.Nation?.ToString();

        var result = await notifyClient.SendEmailAsync(
            "AssessmentInProgress",
            request.PaidByEmail,
            personalisation,
            reference,
            region,
            cancellationToken
        );

        var details = new Dictionary<string, string?>
        {
            ["templateKey"] = "AssessmentInProgress",
            ["recipient"] = request.PaidByEmail,
            ["reference"] = reference,
            ["providerMessageId"] = result.ProviderMessageId,
        };

        if (!result.IsSuccess)
        {
            details["errorMessage"] = result.ErrorMessage;
        }

        // Record the Notify outcome as a separate audit entry (fire-and-forget style:
        // a send failure does not roll back the payment transition).
        // No end-user principal is available for this system entry — pass an empty
        // principal so CreatedBy/CreatedByName are null (correct for a system action).
        var appended = await auditAppender.AppendAsync(
            workItem.Id,
            action: result.IsSuccess ? "notification-sent" : "notification-failed",
            actionDisplayName: result.IsSuccess
                ? "Assessment in progress email sent"
                : "Assessment in progress email failed",
            details,
            user: new ClaimsPrincipal(),
            cancellationToken
        );

        if (!appended)
        {
            logger.LogWarning(
                "Notification outcome audit entry could not be persisted for work item {WorkItemId}.",
                workItem.Id
            );
        }
    }
}
