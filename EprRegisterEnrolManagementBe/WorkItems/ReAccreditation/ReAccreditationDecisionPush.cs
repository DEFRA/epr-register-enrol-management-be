using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// epr-p86e / RA-410: the pre-commit operator-journey push for a final
/// outcome, and the audit entries that record it. Used by RA-527's
/// <see cref="ReAccreditationConfirmContactService"/> (step 2, confirm
/// applicant contact, for both an approval and a refusal); step 1 of a
/// determination makes no push.
///
/// The caller pushes BEFORE it writes anything. When the push fails
/// (after the adapter's own retry budget) the caller abandons the outcome
/// with nothing changed except the <c>status-push-failed</c> entry this
/// records, as the single-step decision did on main. That entry is shown to
/// regulators in the history, not only to support. A disabled push
/// (<see cref="OperatorBackendPushResult.IsSkipped"/>) is a pass — outcomes
/// must still work where the push is switched off. Once the outcome has
/// landed, <see cref="RecordDeliveredAsync"/> records <c>status-push-sent</c>
/// or <c>status-push-skipped</c>, mirroring the entries
/// <see cref="ReAccreditationStatusPushHook"/> writes (it does not fire for
/// these actions), with the same detail keys so management-fe's audit log
/// renders them identically.
/// </summary>
internal sealed class ReAccreditationDecisionPush(
    IOperatorBackendPushAdapter pushAdapter,
    IWorkItemAuditAppender auditAppender,
    ILogger<ReAccreditationDecisionPush> logger
)
{
    private static readonly ReAccreditationType s_type = new();

    /// <summary>
    /// Pushes the outcome to the operator journey. On failure the
    /// <c>status-push-failed</c> entry is already recorded and the caller
    /// must stop.
    /// </summary>
    public async Task<Delivery> PushAsync(
        WorkItem workItem,
        ReAccreditationConfirmContactGuard.Step step,
        string actionDisplayName,
        DateTime occurredAt,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var correlationId = Guid.NewGuid();
        var (_, fromStateId, toStateId, actionId) = step;
        var toStateDisplayName = ResolveStateDisplayName(workItem, toStateId);
        var pushResult = await pushAdapter.PushDecisionStatusChangedAsync(
            workItem.Id,
            correlationId,
            fromStateId,
            toStateId,
            toStateDisplayName,
            actionId,
            actionDisplayName,
            occurredAt,
            cancellationToken
        );
        var delivery = new Delivery(
            workItem.Id,
            correlationId,
            fromStateId,
            toStateId,
            toStateDisplayName,
            actionId,
            actionDisplayName,
            pushResult
        );

        if (!delivery.Failed)
        {
            return delivery;
        }

        logger.LogWarning(
            "Outcome {ActionId} for work item {WorkItemId} abandoned: the operator journey could not be "
                + "notified (correlation {CorrelationId}): {ErrorMessage}. No state was changed.",
            actionId,
            delivery.WorkItemId,
            delivery.CorrelationId,
            pushResult.ErrorMessage
        );
        await AppendAsync(
            delivery,
            "status-push-failed",
            "Status failed to send to the Registration & Accreditation service",
            user,
            cancellationToken,
            extraKey: "errorMessage",
            extraValue: pushResult.ErrorMessage
        );
        return delivery;
    }

    /// <summary>
    /// Records a delivered (or deliberately skipped) push once the outcome
    /// has landed, so it sits after the outcome's own audit entries.
    /// </summary>
    public Task RecordDeliveredAsync(
        Delivery delivery,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    ) =>
        delivery.Result.IsSkipped
            ? AppendAsync(
                delivery,
                "status-push-skipped",
                "Status not sent to the Registration & Accreditation service (disabled)",
                user,
                cancellationToken,
                extraKey: "reason",
                extraValue: delivery.Result.ErrorMessage
            )
            : AppendAsync(
                delivery,
                "status-push-sent",
                "Status sent to the Registration & Accreditation service",
                user,
                cancellationToken
            );

    private async Task AppendAsync(
        Delivery delivery,
        string action,
        string actionDisplayName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken,
        string? extraKey = null,
        string? extraValue = null
    )
    {
        var details = new Dictionary<string, string?>
        {
            ["actionId"] = delivery.ActionId,
            ["actionDisplayName"] = delivery.ActionDisplayName,
            ["fromStateId"] = delivery.FromStateId,
            ["toStateId"] = delivery.ToStateId,
            ["toStateDisplayName"] = delivery.ToStateDisplayName,
            ["correlationId"] = delivery.CorrelationId.ToString(),
        };
        if (extraKey is not null)
        {
            details[extraKey] = extraValue;
        }

        var appended = await auditAppender.AppendAsync(
            delivery.WorkItemId,
            action,
            actionDisplayName,
            details,
            user,
            cancellationToken
        );
        if (!appended)
        {
            logger.LogWarning(
                "{Action} audit entry could not be persisted for work item {WorkItemId} (correlation {CorrelationId}).",
                action,
                delivery.WorkItemId,
                delivery.CorrelationId
            );
        }
    }

    /// <summary>
    /// Resolves the human-readable label for <paramref name="stateId"/> from
    /// the work item's frozen template snapshot (preferred, so historical items
    /// keep their labels) or the live type as a fallback, falling back to the
    /// raw id — mirroring ReAccreditationStatusPushHook's own resolution so the
    /// decision push carries the same display name the hook would send.
    /// </summary>
    private static string ResolveStateDisplayName(WorkItem workItem, string stateId)
    {
        IWorkItemTemplate template = (IWorkItemTemplate?)workItem.TemplateSnapshot ?? s_type;
        var displayName = template
            .States.FirstOrDefault(state =>
                string.Equals(state.Id, stateId, StringComparison.OrdinalIgnoreCase)
            )
            ?.DisplayName;
        return displayName ?? stateId;
    }

    /// <summary>One push: what was sent and how it went.</summary>
    internal sealed record Delivery(
        Guid WorkItemId,
        Guid CorrelationId,
        string FromStateId,
        string ToStateId,
        string ToStateDisplayName,
        string ActionId,
        string ActionDisplayName,
        OperatorBackendPushResult Result
    )
    {
        /// <summary>The push failed outright (a skipped push is not a failure).</summary>
        public bool Failed => !Result.IsSuccess && !Result.IsSkipped;
    }
}
