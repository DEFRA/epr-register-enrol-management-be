using System.Security.Claims;
using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.Utils.Background;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-123: post-action hook that sends a GOV.UK Notify email after
/// each happy-path lifecycle event for a re-accreditation work item.
///
/// Mapping:
/// <list type="bullet">
///   <item>Submission                                  → <c>SubmissionConfirmation</c></item>
///   <item>Action <c>duly-make</c>                     → <c>DulyMade</c></item>
///   <item>Action <c>payment-received</c>               → <c>AssessmentInProgress</c></item>
///   <item>Action <c>sla-extend</c>                    → <c>SlaExtended</c></item>
///   <item>Action <c>approve</c> / <c>reject</c>       → <c>Decision</c></item>
/// </list>
///
/// <para>
/// The Notify call itself is dispatched on
/// <see cref="IBackgroundTaskQueue"/> rather than awaited on the
/// request thread. This keeps the originating HTTP request inside
/// CDP's 5s ingress budget when the Notify endpoint is slow or
/// unreachable, and means a Notify outage cannot stall (or fail) the
/// work-item mutation. A follow-up will move this onto a durable
/// transactional outbox (see bd issue).
/// </para>
///
/// <para>
/// Failures inside the queued callback are recorded as a
/// <c>notification-failed</c> audit entry on the work item and never
/// re-thrown so a Notify outage cannot unwind the originating
/// mutation.
/// </para>
/// </summary>
internal sealed class ReAccreditationNotificationHook(
    INotifyClient notifyClient,
    IWorkItemAuditAppender auditAppender,
    IBackgroundTaskQueue backgroundTaskQueue,
    ILogger<ReAccreditationNotificationHook> logger) : IWorkItemPostActionHook
{
    private static readonly Dictionary<string, (string TemplateKey, string Description)> s_actionTemplates =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["duly-make"] = ("DulyMade", "Application marked duly made"),
            ["payment-received"] = ("AssessmentInProgress", "Assessment started"),
            ["sla-extend"] = ("SlaExtended", "SLA extended"),
            ["approve"] = ("Decision", "Decision recorded: approved"),
            ["reject"] = ("Decision", "Decision recorded: rejected")
        };

    public Task OnSubmittedAsync(
        WorkItem workItem,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (!IsReAccreditation(workItem))
        {
            return Task.CompletedTask;
        }

        return SendAndRecordAsync(
            workItem,
            templateKey: "SubmissionConfirmation",
            description: "Submission confirmation",
            actionId: null,
            user,
            cancellationToken);
    }

    public Task OnActionAppliedAsync(
        WorkItem workItem,
        string actionId,
        string fromStateId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (!IsReAccreditation(workItem))
        {
            return Task.CompletedTask;
        }

        if (!s_actionTemplates.TryGetValue(actionId, out var mapping))
        {
            return Task.CompletedTask;
        }

        return SendAndRecordAsync(workItem, mapping.TemplateKey, mapping.Description, actionId, user, cancellationToken);
    }

    private static bool IsReAccreditation(WorkItem workItem) =>
        string.Equals(workItem.TypeId, ReAccreditationType.Id, StringComparison.OrdinalIgnoreCase);

    private async Task SendAndRecordAsync(
        WorkItem workItem,
        string templateKey,
        string description,
        string? actionId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var payload = DeserialisePayload(workItem);
        var recipient = payload?.OperatorEmail;
        var reference = workItem.Id.ToString();

        if (string.IsNullOrWhiteSpace(recipient))
        {
            logger.LogInformation(
                "Skipping notification for work item {WorkItemId} ({TemplateKey}): payload has no operator email.",
                workItem.Id, templateKey);
            var appended = await auditAppender.AppendAsync(
                workItem.Id,
                action: "notification-skipped",
                actionDisplayName: $"{description} email skipped",
                details: new Dictionary<string, string?>
                {
                    ["templateKey"] = templateKey,
                    ["reference"] = reference,
                    ["reason"] = "missing-operator-email"
                },
                user,
                cancellationToken);
            if (!appended)
            {
                logger.LogWarning(
                    "notification-skipped audit entry could not be persisted for work item {WorkItemId} ({TemplateKey}).",
                    workItem.Id, templateKey);
            }

            return;
        }

        var personalisation = BuildPersonalisation(payload!, workItem, templateKey, actionId);
        var workItemId = workItem.Id;

        // Dispatch the Notify call on the background task queue so the
        // originating HTTP request returns inside CDP's 5s ingress
        // budget regardless of Notify latency. ClaimsPrincipal,
        // personalisation and the workItem id are safe to capture by
        // value into the closure; INotifyClient and
        // IWorkItemAuditAppender are singletons.
        logger.LogInformation(
            "Queueing {Description} notification for work item {WorkItemId} " +
            "(template={TemplateKey}, reference={Reference})",
            description, workItemId, templateKey, reference);

        await backgroundTaskQueue.QueueAsync(
            (_, ct) => DispatchAsync(
                workItemId, templateKey, description, recipient, personalisation, reference, user, ct),
            cancellationToken);
    }

    private async Task DispatchAsync(
        Guid workItemId,
        string templateKey,
        string description,
        string recipient,
        Dictionary<string, string> personalisation,
        string reference,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        NotifySendResult result;
        try
        {
            result = await notifyClient.SendEmailAsync(
                templateKey, recipient, personalisation, reference, cancellationToken);
        }
        catch (Exception ex)
        {
            // Defence in depth: GovukNotifyClient already swallows SDK
            // exceptions and returns a Failure result. If a future
            // INotifyClient impl leaks one, log it and treat as
            // failure so the audit entry still lands.
            logger.LogError(ex,
                "Unexpected exception dispatching {TemplateKey} notification for work item {WorkItemId}",
                templateKey, workItemId);
            result = NotifySendResult.Failure(ex.Message);
        }
        sw.Stop();

        var details = new Dictionary<string, string?>
        {
            ["templateKey"] = templateKey,
            ["recipient"] = recipient,
            ["reference"] = reference,
            ["providerMessageId"] = result.ProviderMessageId
        };

        if (result.IsSuccess)
        {
            logger.LogInformation(
                "Sent {Description} notification for work item {WorkItemId} " +
                "(template={TemplateKey}, providerMessageId={ProviderMessageId}, durationMs={NotifyDurationMs})",
                description, workItemId, templateKey, result.ProviderMessageId, sw.ElapsedMilliseconds);

            var appended = await auditAppender.AppendAsync(
                workItemId,
                action: "notification-sent",
                actionDisplayName: $"{description} email sent",
                details,
                user,
                cancellationToken);
            if (!appended)
            {
                logger.LogWarning(
                    "notification-sent audit entry could not be persisted for work item {WorkItemId} ({TemplateKey}).",
                    workItemId, templateKey);
            }
        }
        else
        {
            details["errorMessage"] = result.ErrorMessage;

            // LogError (not LogWarning) so the failure is surfaced in
            // dev console + CDP error dashboards. Includes the Notify
            // error message so the cause (timeout, 403 wrong key, 400
            // bad template, ...) is visible without having to read the
            // audit log on the work item.
            logger.LogError(
                "Failed to send {Description} notification for work item {WorkItemId} " +
                "(template={TemplateKey}, durationMs={NotifyDurationMs}): {NotifyError}",
                description, workItemId, templateKey, sw.ElapsedMilliseconds, result.ErrorMessage);

            var appended = await auditAppender.AppendAsync(
                workItemId,
                action: "notification-failed",
                actionDisplayName: $"{description} email failed",
                details,
                user,
                cancellationToken);
            if (!appended)
            {
                logger.LogWarning(
                    "notification-failed audit entry could not be persisted for work item {WorkItemId} ({TemplateKey}).",
                    workItemId, templateKey);
            }
        }
    }

    private ReAccreditationPayload? DeserialisePayload(WorkItem workItem)
    {
        try
        {
            return BsonSerializer.Deserialize<ReAccreditationPayload>(workItem.Payload);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to deserialise payload for work item {WorkItemId}; notification will be skipped.",
                workItem.Id);
            return null;
        }
    }

    private static Dictionary<string, string> BuildPersonalisation(
        ReAccreditationPayload payload,
        WorkItem workItem,
        string templateKey,
        string? actionId = null)
    {
        var personalisation = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["organisation_name"] = payload.OrganisationName ?? string.Empty,
            ["registration_number"] = payload.RegistrationNumber ?? string.Empty,
            ["reference"] = workItem.Id.ToString()
        };

        if (string.Equals(templateKey, "Decision", StringComparison.OrdinalIgnoreCase))
        {
            personalisation["decision"] = string.Equals(actionId, "approve", StringComparison.OrdinalIgnoreCase)
                ? "Approved"
                : "Rejected";

            // RA-132: include the accreditation id and start date when an
            // approval has stamped them on the payload, so the Decision
            // template can reference them in its body. Keys are only added
            // when present so the Notify template's "if available" branches
            // see the field as missing rather than empty.
            if (!string.IsNullOrEmpty(payload.AccreditationId))
            {
                personalisation["accreditation_id"] = payload.AccreditationId;
            }
            if (payload.AccreditationStartDate is { } startDate)
            {
                personalisation["accreditation_start_date"] = startDate.ToString("yyyy-MM-dd");
            }
        }

        return personalisation;
    }
}
