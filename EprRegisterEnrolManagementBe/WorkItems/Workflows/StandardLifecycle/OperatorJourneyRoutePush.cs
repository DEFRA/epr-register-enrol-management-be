using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;

namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-495: the push target for a type the operator journey applies on one of its
/// status routes. Every route takes the same body and signing, so a type differs
/// only in the <see cref="OperatorBackendStatusRoute"/> its pushes go to, for both
/// the post-commit status push (RA-368) and the pre-commit decision push at
/// confirm contact (epr-p86e / RA-527).
/// </summary>
internal sealed class OperatorJourneyRoutePush : IOperatorJourneyPush
{
    /// <summary>
    /// Re-accreditation's:
    /// <c>POST api/v1/accreditation-applications/case-management/{workItemId}/status</c>.
    /// </summary>
    public static readonly OperatorJourneyRoutePush AccreditationApplication = new(
        OperatorBackendStatusRoute.AccreditationApplication
    );

    /// <summary>
    /// Add-ORS's, an in-year change request's:
    /// <c>POST api/v1/accreditation-changes/case-management/{workItemId}/status</c>.
    /// </summary>
    public static readonly OperatorJourneyRoutePush ChangeRequest = new(OperatorBackendStatusRoute.ChangeRequest);

    private readonly OperatorBackendStatusRoute _route;

    private OperatorJourneyRoutePush(OperatorBackendStatusRoute route) => _route = route;

    public Task<OperatorBackendPushResult> PushStatusChangedAsync(
        IOperatorBackendPushAdapter adapter,
        OperatorJourneyStatusChange change,
        CancellationToken cancellationToken
    ) => adapter.PushStatusChangedAsync(_route, OperatorBackendPushPipeline.BestEffort, change, cancellationToken);

    public Task<OperatorBackendPushResult> PushDecisionAsync(
        IOperatorBackendPushAdapter adapter,
        OperatorJourneyStatusChange change,
        CancellationToken cancellationToken
    ) => adapter.PushStatusChangedAsync(_route, OperatorBackendPushPipeline.Decision, change, cancellationToken);
}
