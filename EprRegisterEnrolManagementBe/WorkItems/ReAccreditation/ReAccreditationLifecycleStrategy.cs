using System.Security.Claims;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-495: re-accreditation's opt-in to the shared Standard-workflow
/// lifecycle services:
/// <list type="bullet">
///   <item>Push target: the accreditation-application status route
///   (<see cref="ReAccreditationOperatorJourneyPush"/>).</item>
///   <item>Minting: an accreditation number on approval, and the start date,
///   year, SLA stop and publishing at confirm contact
///   (<see cref="ReAccreditationApprovalService"/>), through
///   <see cref="ReAccreditationDeterminationSteps"/>.</item>
///   <item>Notifications: <see cref="ReAccreditationNotificationHook"/>,
///   which the module registers as a post-action hook.</item>
///   <item>Charge source: the payload's <c>chargeAmountPence</c>.</item>
/// </list>
/// The approval and refusal services are optional only so re-accreditation's
/// own duly-making service, which needs neither, can build one.
/// </summary>
internal sealed class ReAccreditationLifecycleStrategy(
    IReAccreditationApprovalService? approvalService = null,
    IReAccreditationRefusalService? refusalService = null,
    ILogger? logger = null
) : IStandardLifecycleStrategy
{
    private static readonly ReAccreditationType s_type = new();

    private readonly ReAccreditationDeterminationSteps? _determinations =
        approvalService is null || refusalService is null
            ? null
            : new ReAccreditationDeterminationSteps(
                approvalService,
                refusalService,
                logger ?? NullLogger.Instance
            );

    public IWorkItemType Type => s_type;

    public IOperatorJourneyPush? OperatorJourneyPush => ReAccreditationOperatorJourneyPush.Instance;

    public IStandardDeterminationSteps Determinations =>
        _determinations
        ?? throw new InvalidOperationException(
            "This re-accreditation lifecycle strategy was built without the approval and refusal services."
        );

    public int? ReadChargePence(WorkItem workItem) => ReAccreditationPayload.ReadChargeAmountPence(workItem);

    /// <summary>
    /// RA-316: deserialise → with-mutate → merge, as the approval service
    /// does. The merge (rather than a wholesale replace) matters: the model is
    /// <c>[BsonIgnoreExtraElements]</c>, so replacing would silently drop every
    /// unmodelled payload key the operator backend sent. A payload the model
    /// cannot read refuses the duly make.
    /// </summary>
    public bool TryStampPaymentDate(WorkItem workItem, DateOnly paymentDate, ILogger logger)
    {
        ReAccreditationPayload payload;
        try
        {
            payload = BsonSerializer.Deserialize<ReAccreditationPayload>(
                workItem.Payload ?? new BsonDocument()
            );
        }
        catch (Exception ex)
            when (ex is BsonSerializationException or FormatException or InvalidCastException)
        {
            logger.LogError(
                ex,
                "Duly making of work item {WorkItemId} aborted: existing payload could not be "
                    + "deserialised. Proceeding would destroy existing payload data.",
                workItem.Id
            );
            return false;
        }

        var updated = payload with { PaymentDate = paymentDate };

        var merged = (workItem.Payload ?? new BsonDocument()).DeepClone().AsBsonDocument;
        merged.Merge(updated.ToBsonDocument(), overwriteExistingElements: true);
        workItem.ReplacePayload(merged);
        return true;
    }
}

/// <summary>
/// RA-495: re-accreditation's determination writes, which are its approval
/// and refusal services. Approval step 1 mints the accreditation number
/// first (refusing an item that already carries one); confirm contact
/// requires a readable payload and, for an approval, that number.
/// </summary>
internal sealed class ReAccreditationDeterminationSteps(
    IReAccreditationApprovalService approvalService,
    IReAccreditationRefusalService refusalService,
    ILogger logger
) : IStandardDeterminationSteps
{
    public async Task<StandardApprovalPreparation> PrepareApprovalAsync(
        WorkItem workItem,
        CancellationToken cancellationToken
    )
    {
        // RA-527 task 007: restored from the single-step approve on main. An
        // item that already carries an accreditation id (other than the known
        // legacy shape) is refused before the mint and the hop, so nothing is
        // minted or written.
        if (
            ReAccreditationAccreditationNumber.RefuseIfAlreadyIssued(workItem.Id, workItem, logger)
            is { } alreadyIssued
        )
        {
            return StandardApprovalPreparation.Refused(alreadyIssued);
        }

        // Minted before anything is written, so a failed mint leaves the item
        // untouched. The number endpoint is idempotent, so a retry after a
        // later failure gets the same number back.
        var correlationId = Guid.NewGuid();
        var accreditationNumber = await approvalService.ResolveAccreditationNumberAsync(
            workItem.Id,
            correlationId,
            cancellationToken
        );

        if (!accreditationNumber.IsSuccess)
        {
            logger.LogError(
                "Log-decision for work item {WorkItemId} abandoned: an accreditation number could not "
                    + "be resolved (correlation {CorrelationId}): {ErrorMessage}. No state was changed.",
                workItem.Id,
                correlationId,
                accreditationNumber.ErrorMessage
            );
            return StandardApprovalPreparation.Refused(
                WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.AccreditationNumberUnavailable,
                    "The re-accreditation decision could not be recorded because an accreditation number "
                        + "could not be obtained. No change was made; try again shortly."
                )
            );
        }

        return new StandardApprovalPreparation(null, accreditationNumber);
    }

    public Task<WorkItemActionResult> RecordApprovedDeterminationAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        StandardApprovalPreparation preparation,
        CancellationToken cancellationToken
    ) =>
        approvalService.RecordApprovedDeterminationAsync(
            workItemId,
            user,
            (AccreditationNumberResult)preparation.Minted!,
            cancellationToken
        );

    public Task<WorkItemActionResult> RecordRefusedDeterminationAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    ) => refusalService.RecordRefusedDeterminationAsync(workItemId, user, cancellationToken);

    public WorkItemActionResult? CheckConfirmContact(
        Guid workItemId,
        WorkItem workItem,
        StandardConfirmContactStep step,
        ILogger? logger
    ) => ReAccreditationConfirmContactGuard.CheckReAccreditationRules(workItemId, workItem, step, logger);

    public Task<WorkItemActionResult> ApproveAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        DateTimeOffset appliedAt,
        CancellationToken cancellationToken
    ) => approvalService.ApproveAsync(workItemId, user, appliedAt, cancellationToken);

    public Task<WorkItemActionResult> RefuseAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        DateTimeOffset appliedAt,
        CancellationToken cancellationToken
    ) => refusalService.RefuseAsync(workItemId, user, appliedAt, cancellationToken);
}
