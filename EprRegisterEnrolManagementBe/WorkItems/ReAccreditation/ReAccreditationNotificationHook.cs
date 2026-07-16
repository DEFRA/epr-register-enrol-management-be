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
///   <item>Action <c>approve</c>                       → <c>Decision</c></item>
///   <item>Action <c>query-during-assessment</c> / <c>query-during-decision</c> → <c>Queried</c></item>
///   <item>Action <c>withdraw</c> / <c>withdraw-during-*</c> → <c>Withdrawn</c></item>
/// </list>
///
/// Note: the DulyMade notification is now sent by
/// <see cref="ReAccreditationDulyMadeHook"/> as part of the automatic
/// submitted→duly-made transition triggered by task completion.
///
/// RA-211: reject is deliberately NOT mapped here — regulators send the
/// rejection notice manually (outside this service) so they can include
/// right-of-appeal detail the automated Decision template doesn't carry.
/// The reject transition itself and its own audit entry are unaffected;
/// this hook simply never fires a Notify call for it.
///
/// Failures are recorded as a <c>notification-failed</c> audit entry
/// on the work item and never re-thrown so a Notify outage cannot
/// unwind the originating mutation.
///
/// RA-240: submission additionally sends a <c>RegulatorSubmission</c> email
/// to the regional regulator shared mailbox (resolved from the work item's
/// nation via <see cref="IRegulatorMailboxResolver"/>) alongside the
/// operator's <c>SubmissionConfirmation</c>.
///
/// RA-237: assignment / re-assignment / unassignment sends an
/// <c>OfficerAssignment</c> email to the same regulator shared mailbox via
/// <see cref="OnAssignmentChangedAsync"/> — assignment is a first-class
/// envelope operation, so <see cref="WorkItemService"/> fans it out through
/// the post-action hooks explicitly.
///
/// When the regulator mailbox is unresolved (Scotland / Wales / NI
/// placeholders until RA-244) the send is skipped and recorded as a
/// <c>notification-skipped</c> audit entry with reason
/// <c>missing-regulator-mailbox</c>; the originating mutation still succeeds.
/// </summary>
internal sealed class ReAccreditationNotificationHook(
    INotifyClient notifyClient,
    IWorkItemAuditAppender auditAppender,
    IRegulatorMailboxResolver regulatorMailboxResolver,
    IWorkItemPersistence persistence,
    ILogger<ReAccreditationNotificationHook> logger
) : IWorkItemPostActionHook
{
    private static readonly Dictionary<
        string,
        (string TemplateKey, string Description)
    > s_actionTemplates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["payment-received"] = ("AssessmentInProgress", "Assessment started"),
        ["sla-extend"] = ("SlaExtended", "SLA extended"),
        ["approve"] = ("Decision", "Decision recorded: approved"),
        ["query-during-assessment"] = ("Queried", "Application queried"),
        ["query-during-decision"] = ("Queried", "Application queried"),
        ["withdraw"] = ("Withdrawn", "Application withdrawn"),
        ["withdraw-during-duly-made"] = ("Withdrawn", "Application withdrawn"),
        ["withdraw-during-assessment"] = ("Withdrawn", "Application withdrawn"),
        ["withdraw-during-decision"] = ("Withdrawn", "Application withdrawn"),
    };

    public async Task OnSubmittedAsync(
        WorkItem workItem,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (!IsReAccreditation(workItem))
        {
            return;
        }

        // Operator-facing confirmation (RA-123).
        await SendAndRecordAsync(
            workItem,
            templateKey: "SubmissionConfirmation",
            description: "Submission confirmation",
            actionId: null,
            user,
            cancellationToken
        );

        // RA-240: regulator-facing submission notification to the regional
        // shared mailbox. Skipped + audited when the nation's mailbox is
        // unconfigured; the submission still succeeds.
        await SendRegulatorEmailAsync(
            workItem,
            templateKey: "RegulatorSubmission",
            description: "Regulator submission",
            extraPersonalisation: null,
            user,
            cancellationToken
        );
    }

    public Task OnActionAppliedAsync(
        WorkItem workItem,
        string actionId,
        string fromStateId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (!IsReAccreditation(workItem))
        {
            return Task.CompletedTask;
        }

        if (!s_actionTemplates.TryGetValue(actionId, out var mapping))
        {
            return Task.CompletedTask;
        }

        return SendAndRecordAsync(
            workItem,
            mapping.TemplateKey,
            mapping.Description,
            actionId,
            user,
            cancellationToken
        );
    }

    public Task OnAssignmentChangedAsync(
        WorkItem workItem,
        WorkItemAssignmentChange change,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (!IsReAccreditation(workItem))
        {
            return Task.CompletedTask;
        }

        // RA-237: describe the change in operator-facing copy for the
        // OfficerAssignment template. officer_name is the (post-change)
        // assignee name — blank on unassign; changed_by is who performed
        // the change. All keys carry empty-string defaults so a missing
        // value never 400s Notify on a referenced placeholder.
        var assignmentEvent = change switch
        {
            WorkItemAssignmentChange.Assigned => "assigned to an officer",
            WorkItemAssignmentChange.Reassigned => "reassigned to a different officer",
            WorkItemAssignmentChange.Unassigned => "unassigned",
            _ => string.Empty
        };

        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["assignment_event"] = assignmentEvent,
            ["officer_name"] = change == WorkItemAssignmentChange.Unassigned
                ? string.Empty
                : workItem.AssignedToName ?? string.Empty,
            ["changed_by"] = workItem.AssignedBy ?? string.Empty
        };

        return SendRegulatorEmailAsync(
            workItem,
            templateKey: "OfficerAssignment",
            description: $"Officer assignment ({assignmentEvent})",
            extraPersonalisation: extra,
            user,
            cancellationToken);
    }

    private static bool IsReAccreditation(WorkItem workItem) =>
        string.Equals(workItem.TypeId, ReAccreditationType.Id, StringComparison.OrdinalIgnoreCase);

    private async Task SendAndRecordAsync(
        WorkItem workItem,
        string templateKey,
        string description,
        string? actionId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var payload = DeserialisePayload(workItem);
        var recipient = payload?.OperatorEmail;
        // RA-248: operators see the human-facing application reference
        // (RA-#########) in the ((reference)) placeholder. Fall back to the
        // internal work-item Guid only for legacy/malformed items missing the
        // reference, so the placeholder is never blank.
        var reference = string.IsNullOrWhiteSpace(payload?.ApplicationReference)
            ? workItem.Id.ToString()
            : payload.ApplicationReference;

        if (string.IsNullOrWhiteSpace(recipient))
        {
            logger.LogInformation(
                "Skipping notification for work item {WorkItemId} ({TemplateKey}): payload has no operator email.",
                workItem.Id,
                templateKey
            );
            var appended = await auditAppender.AppendAsync(
                workItem.Id,
                action: "notification-skipped",
                actionDisplayName: $"{description} email skipped",
                details: new Dictionary<string, string?>
                {
                    ["templateKey"] = templateKey,
                    ["reference"] = reference,
                    ["reason"] = "missing-operator-email",
                },
                user,
                cancellationToken
            );
            if (!appended)
            {
                logger.LogWarning(
                    "notification-skipped audit entry could not be persisted for work item {WorkItemId} ({TemplateKey}).",
                    workItem.Id,
                    templateKey
                );
            }

            return;
        }

        var personalisation = BuildPersonalisation(
            payload!,
            workItem,
            templateKey,
            reference,
            actionId
        );

        // Entry log: surfaces in docker / CDP logs the moment the hook
        // hands off to the Notify client. Combined with the
        // "Notify send starting" entry log in GovukNotifyClient this
        // makes a hanging Notify endpoint diagnosable from logs alone.
        logger.LogInformation(
            "Sending {Description} notification for work item {WorkItemId} "
                + "(template={TemplateKey}, reference={Reference})",
            description,
            workItem.Id,
            templateKey,
            reference
        );

        // RA-211: region drives the reply-to mailbox (NotifyConfig.GetReplyToId);
        // a missing/unresolvable Nation falls back to NotifyConfig.DefaultReplyToId.
        var region = payload!.Nation?.ToString();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await notifyClient.SendEmailAsync(
            templateKey,
            recipient,
            personalisation,
            reference,
            region,
            cancellationToken
        );
        sw.Stop();

        logger.LogInformation(
            "Notification dispatch completed for work item {WorkItemId} "
                + "(template={TemplateKey}, success={NotifySuccess}, durationMs={NotifyDurationMs})",
            workItem.Id,
            templateKey,
            result.IsSuccess,
            sw.ElapsedMilliseconds
        );

        var details = new Dictionary<string, string?>
        {
            ["templateKey"] = templateKey,
            ["recipient"] = recipient,
            ["reference"] = reference,
            ["providerMessageId"] = result.ProviderMessageId,
        };

        if (result.IsSuccess)
        {
            var appended = await auditAppender.AppendAsync(
                workItem.Id,
                action: "notification-sent",
                actionDisplayName: $"{description} email sent",
                details,
                user,
                cancellationToken
            );
            if (!appended)
            {
                logger.LogWarning(
                    "notification-sent audit entry could not be persisted for work item {WorkItemId} ({TemplateKey}).",
                    workItem.Id,
                    templateKey
                );
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
                cancellationToken
            );
            if (!appended)
            {
                logger.LogWarning(
                    "notification-failed audit entry could not be persisted for work item {WorkItemId} ({TemplateKey}).",
                    workItem.Id,
                    templateKey
                );
            }
        }
    }

    /// <summary>
    /// RA-240 / RA-237: send an email to the regional regulator shared mailbox
    /// resolved from the work item's nation. Mirrors
    /// <see cref="SendAndRecordAsync"/>'s audit shape (notification-sent /
    /// notification-failed) but resolves the recipient from
    /// <see cref="IRegulatorMailboxResolver"/> rather than the operator email,
    /// and skips with reason <c>missing-regulator-mailbox</c> (rather than
    /// <c>missing-operator-email</c>) when the nation's mailbox is unconfigured
    /// (Scotland / Wales / NI until RA-244). The originating mutation still
    /// succeeds on skip / failure — this method never throws.
    ///
    /// <paramref name="extraPersonalisation"/> carries template-specific keys
    /// (e.g. the OfficerAssignment event / officer_name / changed_by) merged on
    /// top of the base organisation_name / registration_number / reference.
    /// </summary>
    private async Task SendRegulatorEmailAsync(
        WorkItem workItem,
        string templateKey,
        string description,
        Dictionary<string, string>? extraPersonalisation,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var reference = workItem.Id.ToString();

        // Determine the nation the same way the rest of the module does:
        // ReAccreditationNationRoutingHook stamps payload.nation at submission
        // and ReAccreditationPayload deserialises it here.
        //
        // Ordering caveat: at submission the NationRoutingHook stamps
        // payload.nation onto a *re-fetched* copy of the work item, not the
        // in-memory instance handed to this hook, so the instance we were
        // passed can still lack payload.nation even though it is persisted.
        // Re-read the persisted document (NationRoutingHook is registered
        // before this hook, so its ReplaceAsync has already completed by the
        // time we run) so the regulator send sees the routed nation. Fall back
        // to the passed-in instance if the re-read comes back null (e.g. the
        // item was concurrently deleted).
        var persisted = await persistence.GetByIdAsync(workItem.Id, cancellationToken);
        var payload = DeserialisePayload(persisted ?? workItem);

        var nation = payload?.Nation;
        var recipient = regulatorMailboxResolver.Resolve(nation);

        if (string.IsNullOrWhiteSpace(recipient))
        {
            logger.LogInformation(
                "Skipping {Description} notification for work item {WorkItemId} ({TemplateKey}): " +
                "no configured regulator mailbox for nation {Nation}.",
                description, workItem.Id, templateKey, nation?.ToString() ?? "(none)");
            var skipAppended = await auditAppender.AppendAsync(
                workItem.Id,
                action: "notification-skipped",
                actionDisplayName: $"{description} email skipped",
                details: new Dictionary<string, string?>
                {
                    ["templateKey"] = templateKey,
                    ["reference"] = reference,
                    ["nation"] = nation?.ToString(),
                    ["reason"] = "missing-regulator-mailbox"
                },
                user,
                cancellationToken);
            if (!skipAppended)
            {
                logger.LogWarning(
                    "notification-skipped audit entry could not be persisted for work item {WorkItemId} ({TemplateKey}).",
                    workItem.Id, templateKey);
            }

            return;
        }

        var personalisation = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["organisation_name"] = payload?.OrganisationName ?? string.Empty,
            ["registration_number"] = payload?.RegistrationNumber ?? string.Empty,
            ["reference"] = reference
        };
        if (extraPersonalisation is not null)
        {
            foreach (var (key, value) in extraPersonalisation)
            {
                personalisation[key] = value;
            }
        }

        logger.LogInformation(
            "Sending {Description} notification for work item {WorkItemId} " +
            "(template={TemplateKey}, reference={Reference})",
            description, workItem.Id, templateKey, reference);

        // RA-211: region drives the reply-to mailbox (NotifyConfig.GetReplyToId);
        // pass the same nation we resolved the mailbox from so regulator-facing
        // sends pick up the regional reply-to identity on the same terms as the
        // operator-facing ones. With RegionToReplyToId empty this resolves to
        // DefaultReplyToId (null) — i.e. no override, template sender unchanged.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await notifyClient.SendEmailAsync(
            templateKey,
            recipient,
            personalisation,
            reference,
            nation?.ToString(),
            cancellationToken
        );
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
            ["nation"] = nation?.ToString(),
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
            logger.LogError(
                ex,
                "Failed to deserialise payload for work item {WorkItemId}; notification will be skipped.",
                workItem.Id
            );
            return null;
        }
    }

    /// <summary>
    /// Text of the most recent work-item-level note (one with no TaskId), or an
    /// empty string when there is none. Notify 400s on a referenced placeholder
    /// that is missing but accepts an empty value, so the lifecycle templates
    /// that surface the latest case note (Withdrawn → <c>withdrawal_notes</c>,
    /// Decision → <c>decision_notes</c>) always pass a present, possibly-empty
    /// value. Task-scoped notes are deliberately ignored.
    /// </summary>
    private static string LatestWorkItemNoteText(WorkItem workItem) =>
        workItem
            .Notes?.Where(note => note.TaskId is null)
            .OrderByDescending(note => note.CreatedAt)
            .FirstOrDefault()
            ?.Text
        ?? string.Empty;

    private static Dictionary<string, string> BuildPersonalisation(
        ReAccreditationPayload payload,
        WorkItem workItem,
        string templateKey,
        string reference,
        string? actionId = null
    )
    {
        var personalisation = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["organisation_name"] = payload.OrganisationName ?? string.Empty,
            ["registration_number"] = payload.RegistrationNumber ?? string.Empty,
            ["reference"] = reference,
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
                personalisation["sla_deadline"] = deadline.ToString(
                    "d MMMM yyyy",
                    CultureInfo.GetCultureInfo("en-GB")
                );
            }
        }

        if (string.Equals(templateKey, "Withdrawn", StringComparison.OrdinalIgnoreCase))
        {
            // RA-204: the Withdrawn template body references a
            // ((withdrawal_notes)) placeholder carrying the reason the
            // application was withdrawn (the latest case note captured on the FE
            // withdraw interstitial). See LatestWorkItemNoteText for why the key
            // is always present with an empty-string fallback.
            personalisation["withdrawal_notes"] = LatestWorkItemNoteText(workItem);
        }

        if (string.Equals(templateKey, "Decision", StringComparison.OrdinalIgnoreCase))
        {
            // RA-211: reject no longer maps to a template (see
            // s_actionTemplates), so this branch is only ever reached via
            // "approve" now — no need to branch on actionId here.
            personalisation["decision"] = "Approved";

            // RA-203: the Decision template body references a ((decision_notes))
            // placeholder carrying the latest case note captured on the FE
            // approval interstitial. See LatestWorkItemNoteText for why the key
            // is always present with an empty-string fallback.
            personalisation["decision_notes"] = LatestWorkItemNoteText(workItem);

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
