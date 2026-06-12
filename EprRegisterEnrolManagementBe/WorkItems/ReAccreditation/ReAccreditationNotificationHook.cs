using System.Globalization;
using System.Security.Claims;
using EprRegisterEnrolManagementBe.Notifications;
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
///   <item>Action <c>payment-received</c>               → <c>AssessmentInProgress</c></item>
///   <item>Action <c>sla-extend</c>                    → <c>SlaExtended</c></item>
///   <item>Action <c>approve</c> / <c>reject</c>       → <c>Decision</c></item>
/// </list>
///
/// Note: the DulyMade notification is now sent by
/// <see cref="ReAccreditationDulyMadeHook"/> as part of the automatic
/// submitted→duly-made transition triggered by task completion.
///
/// Failures are recorded as a <c>notification-failed</c> audit entry
/// on the work item and never re-thrown so a Notify outage cannot
/// unwind the originating mutation.
/// </summary>
internal sealed class ReAccreditationNotificationHook(
    INotifyClient notifyClient,
    IWorkItemAuditAppender auditAppender,
    ILogger<ReAccreditationNotificationHook> logger) : IWorkItemPostActionHook
{
    private static readonly Dictionary<string, (string TemplateKey, string Description)> s_actionTemplates =
        new(StringComparer.OrdinalIgnoreCase)
        {
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

        // Entry log: surfaces in docker / CDP logs the moment the hook
        // hands off to the Notify client. Combined with the
        // "Notify send starting" entry log in GovukNotifyClient this
        // makes a hanging Notify endpoint diagnosable from logs alone.
        logger.LogInformation(
            "Sending {Description} notification for work item {WorkItemId} " +
            "(template={TemplateKey}, reference={Reference})",
            description, workItem.Id, templateKey, reference);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await notifyClient.SendEmailAsync(
            templateKey, recipient, personalisation, reference, cancellationToken);
        sw.Stop();

        logger.LogInformation(
            "Notification dispatch completed for work item {WorkItemId} " +
            "(template={TemplateKey}, success={NotifySuccess}, durationMs={NotifyDurationMs})",
            workItem.Id, templateKey, result.IsSuccess, sw.ElapsedMilliseconds);

        var details = new Dictionary<string, string?>
        {
            ["templateKey"] = templateKey,
            ["recipient"] = recipient,
            ["reference"] = reference,
            ["providerMessageId"] = result.ProviderMessageId
        };

        if (result.IsSuccess)
        {
            var appended = await auditAppender.AppendAsync(
                workItem.Id,
                action: "notification-sent",
                actionDisplayName: $"{description} email sent",
                details,
                user,
                cancellationToken);
            if (!appended)
            {
                logger.LogWarning(
                    "notification-sent audit entry could not be persisted for work item {WorkItemId} ({TemplateKey}).",
                    workItem.Id, templateKey);
            }
        }
        else
        {
            details["errorMessage"] = result.ErrorMessage;
            var appended = await auditAppender.AppendAsync(
                workItem.Id,
                action: "notification-failed",
                actionDisplayName: $"{description} email failed",
                details,
                user,
                cancellationToken);
            if (!appended)
            {
                logger.LogWarning(
                    "notification-failed audit entry could not be persisted for work item {WorkItemId} ({TemplateKey}).",
                    workItem.Id, templateKey);
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

        if (string.Equals(templateKey, "SlaExtended", StringComparison.OrdinalIgnoreCase))
        {
            // RA-201: the SlaExtended Notify template body requires a
            // ((sla_deadline)) placeholder. Without it Notify rejects the
            // send with a 400 "Missing personalisation: sla_deadline" and
            // the extend-SLA email never reaches the operator. The deadline
            // is the SLA window end = clock start + target duration,
            // rendered as operator-facing GOV.UK-style copy (e.g.
            // "1 January 2026"). Guard for a missing clock so a malformed
            // item never NREs the hook (after a successful extend the clock
            // is always present).
            if (workItem.SlaClock is { } slaClock)
            {
                // Take the .Date (drop the time-of-day) before formatting so a
                // non-UTC / non-midnight StartedAt cannot shift the rendered
                // deadline onto an adjacent calendar day. For the normal UTC
                // path this is a no-op.
                var deadline = (slaClock.StartedAt + slaClock.TargetDuration).Date;
                personalisation["sla_deadline"] =
                    deadline.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"));
            }
        }

        if (string.Equals(templateKey, "Decision", StringComparison.OrdinalIgnoreCase))
        {
            personalisation["decision"] = string.Equals(actionId, "approve", StringComparison.OrdinalIgnoreCase)
                ? "Approved"
                : "Rejected";

            // RA-203: the Decision Notify template body references a
            // ((decision_notes)) placeholder. Without it Notify rejects the
            // send with a 400 "Missing personalisation: decision_notes" and the
            // approve/reject decision email never reaches the operator. The note
            // is the most recent WORK-ITEM-LEVEL note (TaskId is null) captured
            // on the FE approval interstitial before the transition; task-scoped
            // notes are ignored. The key MUST always be present for this
            // template (Notify 400s on a missing referenced placeholder, but an
            // empty value is fine), so fall back to an empty string when there
            // is no work-item-level note.
            var decisionNote = workItem.Notes?
                .Where(note => note.TaskId is null)
                .OrderByDescending(note => note.CreatedAt)
                .FirstOrDefault();
            personalisation["decision_notes"] = decisionNote?.Text ?? string.Empty;

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
