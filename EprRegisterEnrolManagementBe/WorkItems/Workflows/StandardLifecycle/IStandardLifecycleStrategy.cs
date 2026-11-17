using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-495: what differs between case types that share the
/// <see cref="StandardWorkflow"/> lifecycle services — duly make
/// (<see cref="IStandardDulyMakingService"/>), make determination
/// (<see cref="IStandardLogDecisionService"/>) and confirm applicant contact
/// (<see cref="IStandardConfirmContactService"/>).
///
/// A type opts in by registering one of these (see
/// <see cref="StandardLifecycleServiceCollectionExtensions.AddStandardLifecycle{TStrategy}"/>).
/// A type that has not opted in is refused by the shared services with the
/// same "wrong work item type" failure re-accreditation's own routes always
/// gave.
///
/// Notifications are not a member: a type's emails come from the
/// <see cref="IWorkItemPostActionHook"/>s its module registers (for
/// re-accreditation, <c>ReAccreditationNotificationHook</c>, gated on
/// <c>Notify:Enabled</c>). The shared services fire every registered hook
/// after each write, and each hook reacts only to its own type, so a type
/// whose module registers no email hook sends no email.
/// </summary>
internal interface IStandardLifecycleStrategy : IStandardDulyMakingRules
{
    /// <summary>
    /// Where the operator journey is told about this type's status
    /// changes, both the post-commit status push and the pre-commit decision
    /// push at confirm contact. Null when the operator journey cannot apply this type: nothing is
    /// pushed and no push audit entry is written.
    /// </summary>
    IOperatorJourneyPush? OperatorJourneyPush { get; }

    /// <summary>
    /// The determination writes: step 1 (make determination, including any
    /// accreditation-number minting) and step 2 (confirm applicant contact).
    /// Types with nothing bespoke use <see cref="StandardDeterminationWrites"/>.
    /// </summary>
    IStandardDeterminationSteps Determinations { get; }
}

/// <summary>
/// RA-495: the part of a <see cref="IStandardLifecycleStrategy"/> that duly
/// making needs (<see cref="StandardDulyMakingService"/>): the type, its
/// charge source and its payment-date stamp. Separate so a type's own duly
/// make route can use the shared service without the determination writes.
/// </summary>
internal interface IStandardDulyMakingRules
{
    /// <summary>
    /// The case type. Its <see cref="IWorkItemType.TypeId"/> is the opt-in key,
    /// and it is the template fallback for a legacy work item with no frozen
    /// snapshot.
    /// </summary>
    IWorkItemType Type { get; }

    /// <summary>
    /// The charge, in pence, the operator paid for this work item: the charge
    /// a caseworker matches the payment against when duly making it. Null when
    /// the payload carries none.
    /// </summary>
    int? ReadChargePence(WorkItem workItem);

    /// <summary>
    /// Records the payment date entered at duly making on the payload.
    /// Returns false (logged) when the payload cannot be read, which refuses
    /// the duly make with nothing written. The default writes a top-level
    /// <c>paymentDate</c> string (<c>yyyy-MM-dd</c>), the shape
    /// re-accreditation's model stores.
    /// </summary>
    bool TryStampPaymentDate(WorkItem workItem, DateOnly paymentDate, ILogger logger)
    {
        var payload = (workItem.Payload ?? new BsonDocument()).DeepClone().AsBsonDocument;
        payload[PaymentDateField] = paymentDate.ToString("yyyy-MM-dd");
        workItem.ReplacePayload(payload);
        return true;
    }

    /// <summary>The top-level payload field the payment date is stamped on.</summary>
    const string PaymentDateField = "paymentDate";
}
