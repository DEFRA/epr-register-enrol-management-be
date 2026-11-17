using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-337 / RA-372: "where did this work item come from before it landed in
/// <c>updated</c>, and where is it going back to". Shared by
/// <see cref="ReAccreditationContinueReviewService"/> (which picks the
/// <c>continue-review-during-*</c> action) and
/// <see cref="ReAccreditationOriginStateResolver"/> (which reports the origin
/// on the wire); they must agree.
///
/// RA-495: the resolution itself is a Standard-workflow rule and now lives in
/// <see cref="StandardUpdatedOrigin"/>, which the shared duly make also uses.
/// </summary>
internal static class ReAccreditationUpdatedOrigin
{
    /// <summary>The waypoint state this whole file is about.</summary>
    public const string StateId = StandardUpdatedOrigin.StateId;

    /// <summary>
    /// True when <paramref name="workItem"/> is a re-accreditation item
    /// currently parked in the <c>updated</c> waypoint.
    /// </summary>
    public static bool IsUpdatedReAccreditation(WorkItem workItem) =>
        string.Equals(workItem.TypeId, ReAccreditationType.Id, StringComparison.OrdinalIgnoreCase)
        && string.Equals(workItem.StateId, StateId, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc cref="StandardUpdatedOrigin.ResolveContinueActionId"/>
    public static string? ResolveContinueActionId(WorkItem workItem) =>
        StandardUpdatedOrigin.ResolveContinueActionId(workItem);

    /// <inheritdoc cref="StandardUpdatedOrigin.ResolveOriginatingStateId"/>
    public static string? ResolveOriginatingStateId(WorkItem workItem, IWorkItemTemplate template) =>
        StandardUpdatedOrigin.ResolveOriginatingStateId(workItem, template);
}
