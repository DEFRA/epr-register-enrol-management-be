using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

namespace EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;

/// <summary>
/// RA-495: add-ORS's push target in the operator journey, the change-request
/// status route
/// (<c>POST api/v1/accreditation-changes/case-management/{workItemId}/status</c>),
/// for both the post-commit status push and the pre-commit decision push at
/// confirm contact. Same body and signing as re-accreditation's
/// accreditation-application route.
/// </summary>
internal sealed class AddOverseasSiteOperatorJourneyPush : IOperatorJourneyPush
{
    public static readonly AddOverseasSiteOperatorJourneyPush Instance = new();

    private AddOverseasSiteOperatorJourneyPush() { }

    public Task<OperatorBackendPushResult> PushStatusChangedAsync(
        IOperatorBackendPushAdapter adapter,
        OperatorJourneyStatusChange change,
        CancellationToken cancellationToken
    ) =>
        adapter.PushChangeRequestStatusChangedAsync(
            change.WorkItemId,
            change.CorrelationId,
            change.FromStateId,
            change.ToStateId,
            change.ToStateDisplayName,
            change.ActionId,
            change.ActionDisplayName,
            change.OccurredAt,
            cancellationToken
        );

    public Task<OperatorBackendPushResult> PushDecisionAsync(
        IOperatorBackendPushAdapter adapter,
        OperatorJourneyStatusChange change,
        CancellationToken cancellationToken
    ) =>
        adapter.PushChangeRequestDecisionStatusChangedAsync(
            change.WorkItemId,
            change.CorrelationId,
            change.FromStateId,
            change.ToStateId,
            change.ToStateDisplayName,
            change.ActionId,
            change.ActionDisplayName,
            change.OccurredAt,
            cancellationToken
        );
}
