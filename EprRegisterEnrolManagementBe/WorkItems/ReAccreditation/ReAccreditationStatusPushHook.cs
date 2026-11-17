using System.Security.Claims;
using EprRegisterEnrolManagementBe.Utils.Background;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;
using Microsoft.AspNetCore.HeaderPropagation;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-368: post-action hook that pushes every re-accreditation state
/// transition to the operator backend's accreditation-application status
/// route, in the background, skipping query, withdraw, the pending-contact
/// and assessment-started moves and the decision actions (whose push is the
/// pre-commit gate at confirm contact).
///
/// RA-495: the behaviour is the shared <see cref="StandardStatusPushHook"/>,
/// for re-accreditation with its own push target
/// (<see cref="ReAccreditationOperatorJourneyPush"/>). This keeps the module's
/// registration (and its place in the hook order, before the email hook) and
/// log category.
/// </summary>
internal sealed class ReAccreditationStatusPushHook(
    IBackgroundTaskQueue backgroundTaskQueue,
    HeaderPropagationValues headerPropagationValues,
    ILogger<ReAccreditationStatusPushHook> logger) : IWorkItemPostActionHook
{
    private readonly StandardStatusPushHook _inner = new(
        new ReAccreditationType(),
        ReAccreditationOperatorJourneyPush.Instance,
        backgroundTaskQueue,
        headerPropagationValues,
        logger);

    public Task OnSubmittedAsync(WorkItem workItem, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        _inner.OnSubmittedAsync(workItem, user, cancellationToken);

    public Task OnActionAppliedAsync(
        WorkItem workItem,
        string actionId,
        string fromStateId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken) =>
        _inner.OnActionAppliedAsync(workItem, actionId, fromStateId, user, cancellationToken);

    public Task OnAssignmentChangedAsync(
        WorkItem workItem, WorkItemAssignmentChange change, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        _inner.OnAssignmentChangedAsync(workItem, change, user, cancellationToken);
}
