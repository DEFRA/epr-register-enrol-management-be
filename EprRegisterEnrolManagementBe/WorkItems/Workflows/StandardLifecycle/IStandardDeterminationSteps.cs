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
    /// minting an accreditation number, and the step 1 write that uses it.
    /// Runs after the shared checks and before the <c>submit-for-decision</c>
    /// hop, so a failure here leaves the item untouched.
    /// </summary>
    Task<StandardApprovalPreparation> PrepareApprovalAsync(
        WorkItem workItem,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Step 1 of a refusal: <c>awaiting-decision</c> → <c>rejected-pending-contact</c>.
    /// <paramref name="effectiveDate"/> is the validated AC6 date for a type that
    /// <see cref="IStandardLifecycleStrategy.TakesEffectiveDate"/>, else null.
    /// </summary>
    Task<WorkItemActionResult> RecordRefusedDeterminationAsync(
        Guid workItemId,
        DateOnly? effectiveDate,
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

    /// <summary>
    /// Step 2 of an approval: <c>approved-pending-contact</c> → <c>approved</c> (Granted).
    /// <paramref name="pushedEffectiveDate"/> is the valid-from date the operator journey was just
    /// told (null for a type that takes none). That date can be corrected until this write lands
    /// (RA-800), so a type that takes one refuses with a concurrency conflict if the item no longer
    /// holds it.
    /// </summary>
    Task<WorkItemActionResult> ApproveAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        DateTimeOffset appliedAt,
        DateOnly? pushedEffectiveDate,
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
/// RA-495: step 1 of an approval, <c>awaiting-decision</c> →
/// <c>approved-pending-contact</c>, with whatever the type prepared for it
/// (re-accreditation: the accreditation number it minted) already bound.
/// <c>effectiveDate</c> is the validated AC6 date for a type that
/// <see cref="IStandardLifecycleStrategy.TakesEffectiveDate"/>, else null.
/// </summary>
internal delegate Task<WorkItemActionResult> StandardApprovedDeterminationWrite(
    Guid workItemId,
    DateOnly? effectiveDate,
    ClaimsPrincipal user,
    CancellationToken cancellationToken
);

/// <summary>
/// RA-495: the outcome of <see cref="IStandardDeterminationSteps.PrepareApprovalAsync"/>:
/// either <see cref="Refusal"/>, the failure to hand back (nothing written),
/// or the step 1 write to run once the <c>submit-for-decision</c> hop has
/// landed (<see cref="RecordApprovedDeterminationAsync"/>).
/// </summary>
internal sealed class StandardApprovalPreparation
{
    private readonly StandardApprovedDeterminationWrite? _recordApprovedDetermination;

    private StandardApprovalPreparation(
        WorkItemActionResult? refusal,
        StandardApprovedDeterminationWrite? recordApprovedDetermination
    )
    {
        Refusal = refusal;
        _recordApprovedDetermination = recordApprovedDetermination;
    }

    /// <summary>The failure to hand back, or null when the approval may go ahead.</summary>
    public WorkItemActionResult? Refusal { get; }

    /// <summary>The approval may go ahead with <paramref name="recordApprovedDetermination"/> as its step 1.</summary>
    public static StandardApprovalPreparation Ready(StandardApprovedDeterminationWrite recordApprovedDetermination) =>
        new(null, recordApprovedDetermination);

    public static StandardApprovalPreparation Refused(WorkItemActionResult refusal) => new(refusal, null);

    /// <summary>Step 1 of the approval. Only for a preparation that was not <see cref="Refused"/>.</summary>
    public Task<WorkItemActionResult> RecordApprovedDeterminationAsync(
        Guid workItemId,
        DateOnly? effectiveDate,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    ) =>
        (
            _recordApprovedDetermination
            ?? throw new InvalidOperationException("A refused approval has no step 1 write.")
        )(workItemId, effectiveDate, user, cancellationToken);
}
