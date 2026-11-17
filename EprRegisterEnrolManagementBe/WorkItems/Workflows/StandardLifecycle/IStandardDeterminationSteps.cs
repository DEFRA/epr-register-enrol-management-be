using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-495: a Standard-workflow type's determination writes. The shared
/// services own the order (checks, the internal <c>submit-for-decision</c>
/// hop, the pre-commit push); these own what each write does to the work
/// item. Each write is one <see cref="IWorkItemPersistence.ReplaceAsync"/>
/// that re-checks the item, then fires the post-action hooks.
///
/// Re-accreditation's mints an accreditation number on approval and stamps
/// its start date and year at confirm contact. Types with nothing bespoke use
/// <see cref="StandardDeterminationWrites"/>.
/// </summary>
internal interface IStandardDeterminationSteps
{
    /// <summary>
    /// Anything an approval needs before step 1 writes anything, such as
    /// minting an accreditation number. Runs after the shared checks and
    /// before the <c>submit-for-decision</c> hop, so a failure here leaves the
    /// item untouched.
    /// </summary>
    Task<StandardApprovalPreparation> PrepareApprovalAsync(
        WorkItem workItem,
        CancellationToken cancellationToken
    );

    /// <summary>Step 1 of an approval: <c>awaiting-decision</c> → <c>approved-pending-contact</c>.</summary>
    Task<WorkItemActionResult> RecordApprovedDeterminationAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        StandardApprovalPreparation preparation,
        CancellationToken cancellationToken
    );

    /// <summary>Step 1 of a refusal: <c>awaiting-decision</c> → <c>rejected-pending-contact</c>.</summary>
    Task<WorkItemActionResult> RecordRefusedDeterminationAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// The type's own checks before confirm contact pushes the outcome, on
    /// top of <see cref="StandardConfirmContactGuard"/>'s. Null when the item
    /// may go ahead, otherwise the failure to hand back. Called before the
    /// push, so the operator is never told an outcome the write would refuse.
    /// </summary>
    WorkItemActionResult? CheckConfirmContact(
        Guid workItemId,
        WorkItem workItem,
        StandardConfirmContactStep step,
        ILogger? logger
    );

    /// <summary>Step 2 of an approval: <c>approved-pending-contact</c> → <c>approved</c> (Granted).</summary>
    Task<WorkItemActionResult> ApproveAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        DateTimeOffset appliedAt,
        CancellationToken cancellationToken
    );

    /// <summary>Step 2 of a refusal: <c>rejected-pending-contact</c> → <c>rejected</c>.</summary>
    Task<WorkItemActionResult> RefuseAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        DateTimeOffset appliedAt,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// RA-495: the outcome of <see cref="IStandardDeterminationSteps.PrepareApprovalAsync"/>.
/// <see cref="Refusal"/> is the failure to hand back (nothing written);
/// otherwise <see cref="Minted"/> is whatever the type minted (re-accreditation:
/// its accreditation number result), handed unchanged to its own step 1 write,
/// or null for a type that mints nothing.
/// </summary>
internal sealed record StandardApprovalPreparation(
    WorkItemActionResult? Refusal,
    object? Minted
)
{
    public static readonly StandardApprovalPreparation NothingToPrepare = new(null, null);

    public static StandardApprovalPreparation Refused(WorkItemActionResult refusal) =>
        new(refusal, null);
}
