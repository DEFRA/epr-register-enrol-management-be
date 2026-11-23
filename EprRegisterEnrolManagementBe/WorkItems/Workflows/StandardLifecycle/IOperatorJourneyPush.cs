using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-495: a Standard-workflow type's push target: which operator-journey
/// route its status changes go to. Re-accreditation's goes to the
/// accreditation-application status route; a type the operator journey
/// cannot apply has none (<see cref="IStandardLifecycleStrategy.OperatorJourneyPush"/> is
/// null).
///
/// Both methods have the adapter's contract: they never throw, and a
/// disabled push is <see cref="OperatorBackendPushResult.IsSkipped"/>, not a
/// failure.
/// </summary>
internal interface IOperatorJourneyPush
{
    /// <summary>
    /// The post-commit push of an applied transition, fired in the
    /// background by <see cref="StandardStatusPushHook"/>.
    /// </summary>
    Task<OperatorBackendPushResult> PushStatusChangedAsync(
        IOperatorBackendPushAdapter adapter,
        OperatorJourneyStatusChange change,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// The pre-commit push of a final decision at confirm contact, sent by
    /// <see cref="StandardDecisionPush"/> before anything is written. Uses the
    /// adapter's stricter decision retry pipeline.
    /// </summary>
    Task<OperatorBackendPushResult> PushDecisionAsync(
        IOperatorBackendPushAdapter adapter,
        OperatorJourneyStatusChange change,
        CancellationToken cancellationToken
    );
}

