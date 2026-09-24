using System.Globalization;
using System.Security.Claims;
using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
/// RA-581: <c>payment-received</c> (<c>AssessmentInProgress</c>) and
/// <c>sla-extend</c> (<c>SlaExtended</c>) were removed from the mapping —
/// these two emails are no longer required. Both actions' own audit entries
/// (and, for <c>sla-extend</c>, <see cref="WorkItems.Core.SlaService"/>'s
/// <c>sla-extended</c> entry) are unaffected; this hook simply never fires a
/// Notify call for either.
///
/// Failures are recorded as a <c>notification-failed</c> audit entry
/// on the work item and never re-thrown so a Notify outage cannot
/// unwind the originating mutation.
///
/// RA-240: submission additionally sends an <c>OperatorApplicationSubmission</c>
/// email (RA-581: renamed from <c>RegulatorSubmission</c> in Notify, same
/// GUID) to the regional regulator shared mailbox (resolved from the work
/// item's nation via <see cref="IRegulatorMailboxResolver"/>) alongside the
/// operator's <c>SubmissionConfirmation</c>.
///
/// RA-237: assignment / re-assignment / unassignment sends an
/// <c>OfficerAssignment</c> email to the same regulator shared mailbox via
/// <see cref="OnAssignmentChangedAsync"/> — assignment is a first-class
/// envelope operation, so <see cref="WorkItemService"/> fans it out through
/// the post-action hooks explicitly.
///
/// RA-581: <c>withdraw</c> / <c>withdraw-during-*</c> additionally send an
/// <c>ApplicationWithdrawn</c> email to the regional regulator shared mailbox
/// alongside the operator's <c>Withdrawn</c> email.
///
/// RA-581: <c>resume-during-*</c> (the operator responding to a query, via
/// <c>ReAccreditationResumeService</c>) sends a <c>QueryResponse</c> email to
/// the regulator shared mailbox — regulator-only, there is no operator-facing
/// template for this event.
///
/// RA-581: every regulator-facing template's body now surfaces
/// <c>((reference))</c> and <c>((work_item_link))</c>, so all four
/// (<c>OperatorApplicationSubmission</c>, <c>OfficerAssignment</c>,
/// <c>ApplicationWithdrawn</c>, <c>QueryResponse</c>) use the human-facing
/// <c>RA-#########</c> reference (see <see cref="SendRegulatorEmailAsync"/>'s
/// <c>useHumanFacingReference</c>) and a deep link built from
/// <see cref="CaseManagementConfig"/>.
///
/// When the regulator mailbox is unresolved (Scotland / Wales / NI
/// placeholders until RA-244) the send is skipped and recorded as a
/// <c>notification-skipped</c> audit entry with reason
/// <c>missing-regulator-mailbox</c>; the originating mutation still succeeds.
///
/// RA-581 (AC02): every send additionally checks
/// <see cref="NotifyConfig.IsTriggerEnabled"/> before anything else — a
/// template switched off via <c>Notify:TriggersEnabled</c> is skipped and
/// recorded with reason <c>trigger-disabled</c>, independent of the global
/// <see cref="NotifyConfig.Enabled"/> kill switch.
///
/// RA-581 (AC01/AC03/AC05/AC06): operator-facing personalisation is built
/// per-template in <see cref="BuildPersonalisation"/> rather than from a
/// shared base set — the five current templates do NOT all reference the
/// same placeholders (Withdrawn has no <c>organisation_name</c> at all), and
/// Notify 400s on a surplus key just as readily as a missing one. Every
/// template that has one supplies <c>contactName</c> (AC03: the submitter's
/// name from the case management "additional information" tab, AC06:
/// falling back to the organisation name when blank).
/// </summary>
internal sealed class ReAccreditationNotificationHook(
    INotifyClient notifyClient,
    IWorkItemAuditAppender auditAppender,
    IRegulatorMailboxResolver regulatorMailboxResolver,
    IWorkItemPersistence persistence,
    ILogger<ReAccreditationNotificationHook> logger,
    IOptions<NotifyConfig> notifyOptions,
    IOptions<CaseManagementConfig>? caseManagementOptions = null
) : IWorkItemPostActionHook
{
    private const string ApplicationQueriedDescription = "Application queried";
    private const string ApplicationWithdrawnDescription = "Operator application withdrawn";
    private const string QueriedTemplateKey = "Queried";
    private const string ReferenceKey = "reference";
    private const string WithdrawnTemplateKey = "Withdrawn";

    private readonly NotifyConfig _notifyConfig = notifyOptions.Value;

    /// <summary>
    /// RA-581: base URL of the case management (management-fe) service,
    /// used to build the <c>work_item_link</c> placeholder every
    /// regulator-facing template now carries. Optional so an unconfigured
    /// environment degrades to an empty link rather than failing the send;
    /// see <see cref="CaseManagementConfig"/>.
    /// </summary>
    private readonly string _caseManagementBaseUrl =
        caseManagementOptions?.Value.BaseUrl?.Trim().TrimEnd('/') ?? string.Empty;

    // RA-581: the operator responds to a query on one of these four
    // resume-during-* actions (ReAccreditationResumeService), the inverse of
    // the query-during-* actions below. Routed straight to the regulator's
    // QueryResponse email — there is no operator-facing template for this
    // event, unlike every other row in s_actionTemplates.
    private static readonly HashSet<string> s_queryResponseActions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "resume-during-duly-making",
            "resume-during-duly-made",
            "resume-during-assessment",
            "resume-during-decision",
        };

    private static readonly Dictionary<
        string,
        (string TemplateKey, string Description)
    > s_actionTemplates = new(StringComparer.OrdinalIgnoreCase)
    {
        // RA-316: the DulyMade email used to be sent by the now-deleted
        // ReAccreditationDulyMadeHook, which hand-rolled its own copy of the
        // send/audit logic. Duly making is a transition like any other now, so
        // it routes through this hook's generic path — one code path, and the
        // audit descriptions ("Application marked duly made email sent/failed")
        // come out identical to the ones it replaces.
        ["duly-make"] = ("DulyMade", "Application marked duly made"),
        ["approve"] = ("Decision", "Decision recorded: approved"),
        ["query-during-duly-making"] = (QueriedTemplateKey, ApplicationQueriedDescription),
        ["query-during-duly-made"] = (QueriedTemplateKey, ApplicationQueriedDescription),
        ["query-during-assessment"] = (QueriedTemplateKey, ApplicationQueriedDescription),
        ["query-during-decision"] = (QueriedTemplateKey, ApplicationQueriedDescription),
        ["withdraw"] = (WithdrawnTemplateKey, ApplicationWithdrawnDescription),
        ["withdraw-during-duly-made"] = (WithdrawnTemplateKey, ApplicationWithdrawnDescription),
        ["withdraw-during-assessment"] = (WithdrawnTemplateKey, ApplicationWithdrawnDescription),
        ["withdraw-during-decision"] = (WithdrawnTemplateKey, ApplicationWithdrawnDescription),
        ["withdraw-during-query"] = (WithdrawnTemplateKey, ApplicationWithdrawnDescription),
        // RA-252 (v10): withdrawal from the 'updated' state (i.e. after an
        // operator has responded to a query) — was added to the state
        // machine but never added here, so this transition silently sent
        // no email at all, operator or regulator, until found via local
        // testing (RA-581).
        ["withdraw-during-updated"] = (WithdrawnTemplateKey, ApplicationWithdrawnDescription),
    };

    /// <summary>
    /// RA-581: <c>{CaseManagementBaseUrl}/work-items/{id}</c>, the deep link
    /// every regulator-facing template's <c>work_item_link</c> placeholder
    /// carries. Empty when the base URL is unconfigured, matching every other
    /// optional-link placeholder in this hook.
    /// </summary>
    private string BuildWorkItemLink(Guid workItemId) =>
        string.IsNullOrEmpty(_caseManagementBaseUrl)
            ? string.Empty
            : $"{_caseManagementBaseUrl}/work-items/{workItemId}";

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
            description: "Operator submission confirmation",
            actionId: null,
            user,
            cancellationToken
        );

        // RA-240: regulator-facing submission notification to the regional
        // shared mailbox. Skipped + audited when the nation's mailbox is
        // unconfigured; the submission still succeeds.
        // RA-581: template renamed from RegulatorSubmission to
        // OperatorApplicationSubmission in Notify (same GUID, same content
        // shape) — Notify resolves templates by GUID, not name, so this is
        // purely a config-key rename to keep our naming legible against the
        // Notify portal, not a functional change.
        await SendRegulatorEmailAsync(
            workItem,
            templateKey: "OperatorApplicationSubmission",
            description: "Regulator submission",
            extraPersonalisation: null,
            user,
            cancellationToken,
            useHumanFacingReference: true
        );
    }

    public async Task OnActionAppliedAsync(
        WorkItem workItem,
        string actionId,
        string fromStateId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (!IsReAccreditation(workItem))
        {
            return;
        }

        // RA-581: operator responded to a query — regulator-only, no
        // operator-facing template for this event (see s_queryResponseActions).
        if (s_queryResponseActions.Contains(actionId))
        {
            await SendRegulatorEmailAsync(
                workItem,
                templateKey: "QueryResponse",
                description: "Query response",
                extraPersonalisation: null,
                user,
                cancellationToken,
                useHumanFacingReference: true
            );
            return;
        }

        if (!s_actionTemplates.TryGetValue(actionId, out var mapping))
        {
            return;
        }

        await SendAndRecordAsync(
            workItem,
            mapping.TemplateKey,
            mapping.Description,
            actionId,
            user,
            cancellationToken
        );

        // RA-581: withdrawal additionally notifies the regulator's regional
        // shared mailbox, mirroring OnSubmittedAsync's dual operator +
        // regulator send. Covers every withdraw-during-* action, since they
        // all map to WithdrawnTemplateKey above. Withdrawal_reason mirrors
        // the operator-facing Withdrawn template's own value — same latest
        // case note, same key name.
        if (string.Equals(mapping.TemplateKey, WithdrawnTemplateKey, StringComparison.OrdinalIgnoreCase))
        {
            await SendRegulatorEmailAsync(
                workItem,
                templateKey: "ApplicationWithdrawn",
                description: "Regulator withdrawal notification",
                extraPersonalisation: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Withdrawal_reason"] = LatestWorkItemNoteText(workItem),
                },
                user,
                cancellationToken,
                useHumanFacingReference: true
            );
        }
    }

    public Task OnAssignmentChangedAsync(
        WorkItem workItem,
        WorkItemAssignmentChange change,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
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
            _ => string.Empty,
        };

        // changed_by comes from the acting principal, not workItem.AssignedBy:
        // AssignedBy holds a raw user id rather than a display name, and the
        // engine has already cleared it to null by the time an unassign reaches
        // us. Prefer the human-readable name claim, same precedence as the
        // audit log's createdByName/createdBy.
        var changedBy =
            user.FindFirstValue("user:name") ?? user.FindFirstValue("user:id") ?? string.Empty;

        // RA-581: assignment_event is no longer part of the personalisation
        // dict — the live template dropped it (the body no longer
        // distinguishes assigned/reassigned/unassigned), and Notify 400s on a
        // surplus key just as readily as a missing one. assignmentEvent
        // itself stays: it still drives the audit log's description.
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["officer_name"] =
                change == WorkItemAssignmentChange.Unassigned
                    ? string.Empty
                    : workItem.AssignedToName ?? string.Empty,
            ["changed_by"] = changedBy,
        };

        return SendRegulatorEmailAsync(
            workItem,
            templateKey: "OfficerAssignment",
            description: $"Officer assignment ({assignmentEvent})",
            extraPersonalisation: extra,
            user,
            cancellationToken,
            useHumanFacingReference: true
        );
    }

    private static bool IsReAccreditation(WorkItem workItem) =>
        string.Equals(workItem.TypeId, ReAccreditationType.Id, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// RA-581 (AC02): records a <c>notification-skipped</c> entry with reason
    /// <c>trigger-disabled</c> when <see cref="NotifyConfig.TriggersEnabled"/>
    /// has switched <paramref name="templateKey"/> off. Shared by both send
    /// paths so the audit shape (and the "could not persist" warning) matches
    /// every other skip reason exactly.
    /// </summary>
    private async Task AppendTriggerDisabledSkipAsync(
        WorkItem workItem,
        string templateKey,
        string description,
        string reference,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        logger.LogInformation(
            "Skipping {Description} notification for work item {WorkItemId} ({TemplateKey}): "
                + "trigger disabled via Notify:TriggersEnabled configuration.",
            description,
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
                [ReferenceKey] = reference,
                ["reason"] = "trigger-disabled",
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
    }

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

        if (!_notifyConfig.IsTriggerEnabled(templateKey))
        {
            await AppendTriggerDisabledSkipAsync(
                workItem,
                templateKey,
                description,
                reference,
                user,
                cancellationToken
            );
            return;
        }

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
                    [ReferenceKey] = reference,
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
            [ReferenceKey] = reference,
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
    /// (e.g. OfficerAssignment's officer_name / changed_by, or
    /// ApplicationWithdrawn's Withdrawal_reason) merged on top of the base
    /// organisation_name / registration_number / reference / work_item_link.
    ///
    /// <paramref name="useHumanFacingReference"/> (RA-581): every current
    /// regulator-facing template body now surfaces ((reference)) to the
    /// reader, so every call site passes <c>true</c> — the human-facing
    /// RA-######### value (RA-248), same as the operator-facing templates,
    /// falling back to the internal work-item Guid only when the payload
    /// lacks one. <c>false</c> remains the default for any future
    /// regulator-facing template whose body does not surface a reference.
    /// </summary>
    private async Task SendRegulatorEmailAsync(
        WorkItem workItem,
        string templateKey,
        string description,
        Dictionary<string, string>? extraPersonalisation,
        ClaimsPrincipal user,
        CancellationToken cancellationToken,
        bool useHumanFacingReference = false
    )
    {
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

        var reference = useHumanFacingReference && !string.IsNullOrWhiteSpace(payload?.ApplicationReference)
            ? payload.ApplicationReference
            : workItem.Id.ToString();

        if (!_notifyConfig.IsTriggerEnabled(templateKey))
        {
            await AppendTriggerDisabledSkipAsync(
                workItem,
                templateKey,
                description,
                reference,
                user,
                cancellationToken
            );
            return;
        }

        var nation = payload?.Nation;
        var recipient = regulatorMailboxResolver.Resolve(nation);

        if (string.IsNullOrWhiteSpace(recipient))
        {
            logger.LogInformation(
                "Skipping {Description} notification for work item {WorkItemId} ({TemplateKey}): "
                    + "no configured regulator mailbox for nation {Nation}.",
                description,
                workItem.Id,
                templateKey,
                nation?.ToString() ?? "(none)"
            );
            var skipAppended = await auditAppender.AppendAsync(
                workItem.Id,
                action: "notification-skipped",
                actionDisplayName: $"{description} email skipped",
                details: new Dictionary<string, string?>
                {
                    ["templateKey"] = templateKey,
                    [ReferenceKey] = reference,
                    ["nation"] = nation?.ToString(),
                    ["reason"] = "missing-regulator-mailbox",
                },
                user,
                cancellationToken
            );
            if (!skipAppended)
            {
                logger.LogWarning(
                    "notification-skipped audit entry could not be persisted for work item {WorkItemId} ({TemplateKey}).",
                    workItem.Id,
                    templateKey
                );
            }

            return;
        }

        var personalisation = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["organisation_name"] = payload?.OrganisationName ?? string.Empty,
            ["registration_number"] = payload?.RegistrationNumber ?? string.Empty,
            [ReferenceKey] = reference,
            // RA-581: every regulator-facing template now links back to the
            // case in management-fe. Empty when CASE_MANAGEMENT_BASE_URL is
            // unconfigured, same degrade-gracefully treatment as every other
            // optional link this hook builds.
            ["work_item_link"] = BuildWorkItemLink(workItem.Id),
        };
        if (extraPersonalisation is not null)
        {
            foreach (var (key, value) in extraPersonalisation)
            {
                personalisation[key] = value;
            }
        }

        logger.LogInformation(
            "Sending {Description} notification for work item {WorkItemId} "
                + "(template={TemplateKey}, reference={Reference})",
            description,
            workItem.Id,
            templateKey,
            reference
        );

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
            [ReferenceKey] = reference,
            ["nation"] = nation?.ToString(),
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
    /// Text of the most recent note on the work item, or an empty string when
    /// there is none. Notify 400s on a referenced placeholder that is missing
    /// but accepts an empty value, so the Withdrawn template's
    /// <c>withdrawal_reason</c> placeholder always gets a present,
    /// possibly-empty value.
    /// </summary>
    private static string LatestWorkItemNoteText(WorkItem workItem) =>
        workItem.Notes?.OrderByDescending(note => note.CreatedAt).FirstOrDefault()?.Text
        ?? string.Empty;

    /// <summary>
    /// RA-581 (AC03/AC06): the operator's contact name, from
    /// <c>payload.submitterContactDetails.fullName</c> (the case management
    /// "additional information" tab, RA-480), falling back to the
    /// organisation name when blank or the payload predates RA-480.
    /// </summary>
    private static string ResolveContactName(ReAccreditationPayload payload) =>
        string.IsNullOrWhiteSpace(payload.SubmitterContactDetails?.FullName)
            ? payload.OrganisationName ?? string.Empty
            : payload.SubmitterContactDetails.FullName;

    /// <summary>
    /// RA-581: operator-facing date formatting shared by every template that
    /// surfaces a date (SubmissionConfirmation's <c>date</c>, DulyMade's
    /// <c>SubmissionDate</c>, Queried's <c>Querieddate</c>) — GOV.UK-style
    /// "1 January 2026". Empty for a missing date rather than throwing:
    /// Notify accepts an empty value for a referenced placeholder, just not
    /// an absent key.
    /// </summary>
    private static string FormatOperatorFacingDate(DateTime? date) =>
        date is { } value
            ? value.Date.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"))
            : string.Empty;

    /// <summary>
    /// RA-581: every operator-facing template's personalisation, built
    /// per-template rather than from a shared base set — Notify 400s on a
    /// surplus key just as readily as a missing one, and the five current
    /// templates do NOT all reference the same placeholders (e.g. Withdrawn
    /// has no <c>organisation_name</c> at all). See NotifyTemplateContract
    /// for the declarative version of this same set, asserted against in
    /// NotifyTemplateContractTests / NotifyLiveTemplateContractTests.
    /// </summary>
    private Dictionary<string, string> BuildPersonalisation(
        ReAccreditationPayload payload,
        WorkItem workItem,
        string templateKey,
        string reference,
        string? actionId = null
    )
    {
        var contactName = ResolveContactName(payload);
        var organisationName = payload.OrganisationName ?? string.Empty;
        var registrationNumber = payload.RegistrationNumber ?? string.Empty;
        // RA-581: the accreditation year the application is FOR (not the
        // post-approval AccreditationYear stamping) — epr-register-enrol-backend
        // sends this as `accreditationYear` in the submission payload itself
        // (HttpCaseWorkingApiAdapter.BuildPayload), so it is already present
        // from submission onward, not only after approval.
        var year = payload.AccreditationYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var material = payload.Material ?? string.Empty;
        // RA-581: the regulator's shared mailbox address shown to the
        // operator as a contact point — the SAME resolver used to pick the
        // regulator-facing recipient, just read here rather than sent to.
        var regulatorEmail = regulatorMailboxResolver.Resolve(payload.Nation) ?? string.Empty;

        if (string.Equals(templateKey, "SubmissionConfirmation", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["year"] = year,
                ["contactName"] = contactName,
                ["organisation_name"] = organisationName,
                ["material"] = material,
                // AC05: UK vs non-UK sites — no SiteName capture exists for
                // the primary/main application (only overseas sites have
                // one), so this is blank for every current send. SiteAddress
                // is the full address epr-register-enrol-backend sends as
                // `siteAddress` at submission (ReAccreditationPayload.SiteAddress),
                // separate from the postcode-only SiteAddressPostcode.
                ["SiteName"] = string.Empty,
                ["SiteAddress"] = payload.SiteAddress ?? string.Empty,
                ["regulatorName"] = _notifyConfig.GetRegulatorName(payload.Nation?.ToString()) ?? string.Empty,
                ["date"] = FormatOperatorFacingDate(workItem.SubmittedAt),
                ["reference"] = reference,
                ["registration_number"] = registrationNumber,
            };
        }

        if (string.Equals(templateKey, "DulyMade", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["contactName"] = contactName,
                ["year"] = year,
                ["organisation_name"] = organisationName,
                ["material"] = material,
                // "made on" — the original submission date, not the date duly
                // making itself completed.
                ["SubmissionDate"] = FormatOperatorFacingDate(workItem.SubmittedAt),
                ["reference"] = reference,
                ["registration_number"] = registrationNumber,
                ["regulatorEmail"] = regulatorEmail,
            };
        }

        if (string.Equals(templateKey, QueriedTemplateKey, StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["contactName"] = contactName,
                ["year"] = year,
                ["organisation_name"] = organisationName,
                // RA-291: the date the CURRENT query was raised, stamped by
                // ReAccreditationQueryService immediately before the query
                // transition — see CurrentQuery.RaisedAt.
                ["Querieddate"] = FormatOperatorFacingDate(payload.CurrentQuery?.RaisedAt),
                ["reference"] = reference,
                ["registration_number"] = registrationNumber,
                ["regulatorEmail"] = regulatorEmail,
            };
        }

        if (string.Equals(templateKey, "Decision", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["contactName"] = contactName,
                ["year"] = year,
                ["organisation_name"] = organisationName,
                ["material"] = material,
                ["reference"] = reference,
                ["registration_number"] = registrationNumber,
                ["regulatorEmail"] = regulatorEmail,
            };
        }

        if (string.Equals(templateKey, WithdrawnTemplateKey, StringComparison.OrdinalIgnoreCase))
        {
            // RA-204 / RA-581: no organisation_name, year, or material —
            // deliberately the thinnest of the five templates.
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["contactName"] = contactName,
                ["reference"] = reference,
                ["registration_number"] = registrationNumber,
                ["withdrawal_reason"] = LatestWorkItemNoteText(workItem),
            };
        }

        // Unreachable for any template s_actionTemplates/OnSubmittedAsync
        // currently maps to; a base envelope rather than throwing keeps a
        // future new template from crashing the hook before its exact
        // personalisation shape is known.
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["organisation_name"] = organisationName,
            ["registration_number"] = registrationNumber,
            ["reference"] = reference,
        };
    }
}
