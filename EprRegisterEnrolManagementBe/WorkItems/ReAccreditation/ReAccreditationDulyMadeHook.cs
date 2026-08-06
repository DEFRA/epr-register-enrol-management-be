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
    ReAccreditationStatusPushHook statusPushHook,
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

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // RA-372: stateId is the *effective* task state and may differ from
        // workItem.StateId. The submitted-state checklist can be worked while
        // the application is parked in the 'updated' waypoint — an operator
        // has answered a query that was raised during duly-making. Discharge
        // the waypoint through its declared continue-review edge before
        // marking the application duly made, rather than jumping straight
        // from 'updated' to 'duly-made'.
        //
        // Jumping would traverse an edge ReAccreditationType does not declare:
        // the only declared exits from 'updated' are continue-review-during-*
        // and withdraw-during-updated. Nothing would validate it, and the
        // resulting fromStateId ('updated') would land in the audit trail and
        // go on the wire to the operator backend via
        // ReAccreditationStatusPushHook — a from/to pair neither management-fe
        // nor the journey tests model, because it is not in the template both
        // of them mirror.
        //
        // Going through the declared edge keeps the state machine closed and
        // leaves every consumer seeing only transitions it already knows:
        //   updated →(continue-review-during-duly-making) submitted
        //           →(duly-make) duly-made
        //
        // Recorded in memory and committed by the single ReplaceAsync below,
        // NOT as its own persisted step. That is a hard constraint, not a
        // style choice: this method already holds a loaded WorkItem and
        // ReplaceAsync is guarded by an optimistic-concurrency check on
        // WorkItem.Version. Any out-of-band write between our load and our
        // save moves the stored version on and makes our save throw
        // WorkItemConcurrencyException — and the status push below writes one,
        // because IWorkItemAuditAppender.AppendAsync re-reads and replaces the
        // document to record the push outcome. Persisting the discharge
        // separately so it could be pushed separately therefore left the
        // application stranded in 'submitted' with every task complete and the
        // caseworker looking at a 500. Everything that mutates this instance
        // must land in one write, before any push.
        var dischargedWaypoint = string.Equals(
            workItem.StateId,
            ReAccreditationUpdatedOrigin.StateId,
            StringComparison.OrdinalIgnoreCase
        );
        if (dischargedWaypoint)
        {
            DischargeUpdatedWaypoint(workItem, user, now);
        }

        var fromStateId = workItem.StateId;
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

        // RA-368: this transition mutates state directly rather than going
        // through WorkItemService.ApplyActionAsync, so it bypasses the
        // generic engine's post-action hook fan-out — call the status push
        // hook explicitly so the operator backend still learns about it.
        await statusPushHook.OnActionAppliedAsync(workItem, "duly-make", fromStateId, user, cancellationToken);

        logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) auto-transitioned submitted→duly-made "
                + "after all submitted-state tasks were completed"
                + "{WaypointDischarge}.",
            workItem.Id,
            workItem.TypeId,
            dischargedWaypoint
                ? ", having first left the 'updated' waypoint via continue-review-during-duly-making"
                : string.Empty
        );

        await SendDulyMadeNotificationAsync(workItem, user, cancellationToken);
    }

    /// <summary>
    /// RA-372: carry a work item out of the <c>updated</c> waypoint via its
    /// declared <c>continue-review-during-duly-making</c> transition, so the
    /// duly-made transition that follows starts from <c>submitted</c> — a
    /// from-state every downstream consumer already models.
    ///
    /// The action id is fixed rather than derived because only one value is
    /// reachable here: this runs when the effective task state is
    /// <c>submitted</c> (so the query was raised during duly-making) and the
    /// item is in <c>updated</c>, which is precisely the pairing
    /// <c>continue-review-during-duly-making</c> describes. A snapshot old
    /// enough to lack that transition could not have produced the effective
    /// state in the first place — <see cref="ReAccreditationTaskStateResolver"/>
    /// resolves the origin through the very same transition and abstains when
    /// it is absent — so this cannot invent an edge the template lacks.
    ///
    /// Mutates in memory only. The caller commits this together with the
    /// duly-made transition in a single <see cref="IWorkItemPersistence.ReplaceAsync"/>
    /// — see the concurrency note at the call site for why a separate write
    /// here is not an option.
    ///
    /// A consequence worth naming: this transition is therefore not pushed to
    /// the operator backend on its own.
    /// <see cref="ReAccreditationStatusPushHook"/> reads the destination state
    /// off <see cref="WorkItem.StateId"/> at push time, and the push must
    /// happen after the save, by which point the item is already
    /// <c>duly-made</c> — so pushing this separately could only report it as
    /// ending somewhere it did not. The operator backend instead sees the one
    /// push it can act on, <c>duly-make (submitted → duly-made)</c>, which is
    /// a declared pair. Nothing is lost that it could use: <c>submitted</c>
    /// here is a transient waypoint discharge that exists for the duration of
    /// this method, not a stage of the application anyone can observe. The
    /// full two-step path stays visible in the audit log, which is the
    /// regulator-facing record.
    /// </summary>
    private static void DischargeUpdatedWaypoint(
        WorkItem workItem,
        ClaimsPrincipal user,
        DateTime now
    )
    {
        const string ContinueReviewActionId = "continue-review-during-duly-making";

        var fromStateId = workItem.StateId;
        workItem.StateId = "submitted";
        workItem.LastModifiedAt = now;
        workItem.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = "action-applied",
                ActionDisplayName = "Action applied",
                Details = new Dictionary<string, string?>
                {
                    ["actionId"] = ContinueReviewActionId,
                    // Mirrors the DisplayName ReAccreditationType declares for
                    // this transition, matching how the duly-make entry below
                    // states its own display name inline.
                    ["actionDisplayName"] = "Continue review",
                    ["fromStateId"] = fromStateId,
                    ["toStateId"] = workItem.StateId,
                },
                CreatedAt = now,
                CreatedBy = user.FindFirstValue("user:id"),
                CreatedByName = user.FindFirstValue("user:name"),
            }
        );
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
