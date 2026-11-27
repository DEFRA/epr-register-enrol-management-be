using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-495 (from epr-p86e / RA-410 / RA-527): the pre-commit operator-journey
/// push for a final outcome at confirm contact, and the audit entries that
/// record it, for any Standard-workflow type. The push goes to the type's own
/// target (<see cref="IStandardLifecycleStrategy.OperatorJourneyPush"/>).
///
/// The caller pushes BEFORE it writes anything. When the push fails (after
/// the adapter's own retry budget) the caller abandons the outcome with
/// nothing changed except the <c>status-push-failed</c> entry this records.
/// That entry is shown to regulators in the history. A disabled push
/// (<see cref="OperatorBackendPushResult.IsSkipped"/>) is a pass: outcomes
/// must still work where the push is switched off. Once the outcome has
/// landed, <see cref="RecordDeliveredAsync"/> records <c>status-push-sent</c>
/// or <c>status-push-skipped</c>, with the same detail keys
/// <see cref="StandardStatusPushHook"/> writes, so management-fe's audit log
/// renders them identically.
///
/// The push carries the effective date the caller names (RA-495 AC6,
/// <see cref="StandardEffectiveDate"/>): the one the determination stamped, so
/// the operator journey grants an add-ORS site from the date the caseworker
/// entered, or the type's automatic one (RA-782: an ORS update's approval
/// date). A type with neither sends none.
///
/// A type with no push target (the operator journey cannot apply it) is never pushed: the
/// delivery is <see cref="Delivery.NotPushed"/> and nothing is recorded.
/// </summary>
internal class StandardDecisionPush(
    IOperatorBackendPushAdapter pushAdapter,
    IWorkItemAuditAppender auditAppender,
    ILogger logger
)
{
    /// <summary>
    /// What a push is recorded as: the action's display name and when it
    /// happened, and the effective date it carries (null for none).
    /// </summary>
    public readonly record struct PushDetails(string ActionDisplayName, DateTime OccurredAt, DateOnly? EffectiveDate);

    /// <summary>
    /// Pushes the outcome to the operator journey through
    /// <paramref name="target"/>. On failure the <c>status-push-failed</c>
    /// entry is already recorded and the caller must stop.
    /// <paramref name="template"/> is the work item's template, resolved by the
    /// caller as the engine resolves it
    /// (<see cref="WorkItemEngineRules.ResolveTemplate"/>); it supplies the
    /// pushed state's label.
    /// </summary>
    public async Task<Delivery> PushAsync(
        WorkItem workItem,
        IWorkItemTemplate? template,
        IOperatorJourneyPush? target,
        StandardConfirmContactStep step,
        PushDetails details,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (target is null)
        {
            return Delivery.NotPushed(workItem.Id);
        }

        var correlationId = Guid.NewGuid();
        var (_, fromStateId, toStateId, actionId) = step;
        var change = new OperatorJourneyStatusChange(
            workItem.Id,
            correlationId,
            fromStateId,
            toStateId,
            ResolveStateDisplayName(template, toStateId),
            actionId,
            details.ActionDisplayName,
            details.OccurredAt,
            details.EffectiveDate
        );
        var pushResult = await target.PushDecisionAsync(pushAdapter, change, cancellationToken);
        var delivery = new Delivery(change, pushResult);

        if (!delivery.Failed)
        {
            return delivery;
        }

        logger.LogWarning(
            "Outcome {ActionId} for work item {WorkItemId} abandoned: the operator journey could not be "
                + "notified (correlation {CorrelationId}): {ErrorMessage}. No state was changed.",
            actionId,
            workItem.Id,
            correlationId,
            pushResult.ErrorMessage
        );
        await AppendAsync(
            change,
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
    /// has landed, so it sits after the outcome's own audit entries. Records
    /// nothing for a type that is never pushed.
    /// </summary>
    public Task RecordDeliveredAsync(
        Delivery delivery,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (delivery.Change is not { } change)
        {
            return Task.CompletedTask;
        }

        return delivery.Result.IsSkipped
            ? AppendAsync(
                change,
                "status-push-skipped",
                "Status not sent to the Registration & Accreditation service (disabled)",
                user,
                cancellationToken,
                extraKey: "reason",
                extraValue: delivery.Result.ErrorMessage
            )
            : AppendAsync(
                change,
                "status-push-sent",
                "Status sent to the Registration & Accreditation service",
                user,
                cancellationToken
            );
    }

    private async Task AppendAsync(
        OperatorJourneyStatusChange change,
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
            ["actionId"] = change.ActionId,
            ["actionDisplayName"] = change.ActionDisplayName,
            ["fromStateId"] = change.FromStateId,
            ["toStateId"] = change.ToStateId,
            ["toStateDisplayName"] = change.ToStateDisplayName,
            ["correlationId"] = change.CorrelationId.ToString(),
        };
        if (extraKey is not null)
        {
            details[extraKey] = extraValue;
        }

        var appended = await auditAppender.AppendAsync(
            change.WorkItemId,
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
                change.WorkItemId,
                change.CorrelationId
            );
        }
    }

    /// <summary>
    /// The label for <paramref name="stateId"/> from the work item's resolved
    /// <paramref name="template"/> (its frozen snapshot, so historical items
    /// keep their labels, else the live registered type), or the raw id when
    /// there is no template or it does not know the state.
    /// </summary>
    internal static string ResolveStateDisplayName(IWorkItemTemplate? template, string stateId)
    {
        var displayName = template
            ?.States.FirstOrDefault(state =>
                string.Equals(state.Id, stateId, StringComparison.OrdinalIgnoreCase)
            )
            ?.DisplayName;
        return displayName ?? stateId;
    }

    /// <summary>
    /// One push: what was sent and how it went. <see cref="Change"/> is null
    /// when the type is never pushed.
    /// </summary>
    internal sealed record Delivery(
        Guid WorkItemId,
        OperatorJourneyStatusChange? Change,
        OperatorBackendPushResult Result
    )
    {
        public Delivery(OperatorJourneyStatusChange change, OperatorBackendPushResult result)
            : this(change.WorkItemId, change, result) { }

        /// <summary>The push failed outright (a skipped or absent push is not a failure).</summary>
        public bool Failed => !Result.IsSuccess && !Result.IsSkipped;

        public static Delivery NotPushed(Guid workItemId) =>
            new(workItemId, null, OperatorBackendPushResult.Skipped("No operator journey push for this type."));
    }
}
