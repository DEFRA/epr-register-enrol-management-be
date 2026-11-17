using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-316 default <see cref="IReAccreditationDulyMakingService"/>, behind
/// <c>POST /work-items/re-accreditation/{id}/duly-make</c>.
///
/// RA-495: duly making is the shared <see cref="StandardDulyMakingService"/>,
/// here limited to re-accreditation (any other type is refused as before) and
/// using re-accreditation's payload stamp
/// (<see cref="ReAccreditationLifecycleStrategy"/>). The submitted →
/// duly-made transition, the payment date on the payload, the SLA clock
/// anchored to midnight UTC of that date, the audit entries and the
/// post-action hooks (DulyMade email, status push) are unchanged.
/// </summary>
internal sealed class ReAccreditationDulyMakingService(
    IWorkItemPersistence persistence,
    IWorkItemRegistry registry,
    IEnumerable<IWorkItemPostActionHook> postActionHooks,
    TimeProvider timeProvider,
    ILogger<ReAccreditationDulyMakingService> logger
) : IReAccreditationDulyMakingService
{
    private readonly StandardDulyMakingService _shared = new(
        persistence,
        registry,
        postActionHooks,
        timeProvider,
        logger,
        StandardLifecycleStrategies.Of(new ReAccreditationLifecycleStrategy())
    );

    public Task<WorkItemActionResult> CompleteDulyMakingAsync(
        Guid workItemId,
        DateOnly paymentDate,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    ) => _shared.CompleteDulyMakingAsync(workItemId, paymentDate, user, cancellationToken);
}
