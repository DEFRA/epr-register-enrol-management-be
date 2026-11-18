using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-495: re-accreditation's push target in the operator journey, the
/// accreditation-application status route
/// (<c>POST api/v1/accreditation-applications/case-management/{workItemId}/status</c>),
/// for both the post-commit status push (RA-368) and the pre-commit decision
/// push at confirm contact (epr-p86e / RA-527).
/// </summary>
internal sealed class ReAccreditationOperatorJourneyPush : IOperatorJourneyPush
{
    public static readonly ReAccreditationOperatorJourneyPush Instance = new();

    private ReAccreditationOperatorJourneyPush() { }

    public Task<OperatorBackendPushResult> PushStatusChangedAsync(
        IOperatorBackendPushAdapter adapter,
        OperatorJourneyStatusChange change,
        CancellationToken cancellationToken
    ) =>
        adapter.PushStatusChangedAsync(
            OperatorBackendStatusRoute.AccreditationApplication,
            OperatorBackendPushPipeline.BestEffort,
            change,
            cancellationToken
        );

    public Task<OperatorBackendPushResult> PushDecisionAsync(
        IOperatorBackendPushAdapter adapter,
        OperatorJourneyStatusChange change,
        CancellationToken cancellationToken
    ) =>
        adapter.PushStatusChangedAsync(
            OperatorBackendStatusRoute.AccreditationApplication,
            OperatorBackendPushPipeline.Decision,
            change,
            cancellationToken
        );
}
