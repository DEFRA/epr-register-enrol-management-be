using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-337 / RA-372: shared resolution of "where did this work item come from
/// before it landed in <c>updated</c>, and where is it going back to".
///
/// The <c>updated</c> state is a waypoint. A queried application returns to it
/// when the operator responds, and it carries no identity of its own — every
/// question worth asking about an item sitting there ("which tasks apply?",
/// "which continue-review action moves it on?") is really a question about the
/// state the query was raised from.
///
/// That originating state is not stored on the document. It is derived from
/// the item's own audit history, which is deliberate: the audit log is the
/// record of what actually happened, so deriving from it cannot drift out of
/// sync with a denormalised copy, and it needs no migration to work on items
/// that are already in flight.
///
/// Two callers share this, and they must agree — if they disagreed, a
/// caseworker could complete one state's tasks and then be moved into a
/// different one:
/// <list type="bullet">
///   <item><see cref="ReAccreditationContinueReviewService"/>, to pick the
///   <c>continue-review-during-*</c> action that moves the item on.</item>
///   <item><see cref="ReAccreditationTaskStateResolver"/>, to pick whose task
///   list the engine should project and score against.</item>
/// </list>
/// </summary>
internal static class ReAccreditationUpdatedOrigin
{
    /// <summary>The waypoint state this whole file is about.</summary>
    public const string StateId = "updated";

    /// <summary>
    /// Inverse of the <c>resume-during-*</c> action that put a work item into
    /// <c>updated</c>: which <c>continue-review-during-*</c> action carries it
    /// on to the state it was originally queried from.
    /// </summary>
    private static readonly Dictionary<string, string> s_continueActionByResumeAction =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["resume-during-duly-making"] = "continue-review-during-duly-making",
            ["resume-during-duly-made"] = "continue-review-during-duly-made",
            ["resume-during-assessment"] = "continue-review-during-assessment",
            ["resume-during-decision"] = "continue-review-during-decision",
        };

    /// <summary>
    /// True when <paramref name="workItem"/> is a re-accreditation item
    /// currently parked in the <c>updated</c> waypoint.
    /// </summary>
    public static bool IsUpdatedReAccreditation(WorkItem workItem) =>
        string.Equals(workItem.TypeId, ReAccreditationType.Id, StringComparison.OrdinalIgnoreCase)
        && string.Equals(workItem.StateId, StateId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>continue-review-during-*</c> action that moves
    /// <paramref name="workItem"/> out of <c>updated</c>, or <c>null</c> when
    /// it cannot be determined.
    ///
    /// Reads the most recent generic <c>action-applied</c> audit entry. While
    /// an item is in <c>updated</c> that entry is necessarily the
    /// <c>resume-during-*</c> transition that put it there: the only declared
    /// ways out of <c>updated</c> are <c>continue-review-during-*</c> and
    /// <c>withdraw-during-updated</c>, and both leave the state, so no later
    /// action entry can exist while the item is still here.
    /// </summary>
    public static string? ResolveContinueActionId(WorkItem workItem)
    {
        var resumeActionId = workItem
            .AuditLog.Where(entry =>
                string.Equals(entry.Action, "action-applied", StringComparison.Ordinal)
            )
            .OrderByDescending(entry => entry.CreatedAt)
            .Select(entry => entry.Details.GetValueOrDefault("actionId"))
            .FirstOrDefault(actionId => actionId is not null);

        return resumeActionId is not null
            && s_continueActionByResumeAction.TryGetValue(resumeActionId, out var continueActionId)
            ? continueActionId
            : null;
    }

    /// <summary>
    /// The state <paramref name="workItem"/> was queried from — equivalently,
    /// the state <c>continue-review</c> will return it to — or <c>null</c> if
    /// it cannot be determined.
    ///
    /// Resolved through the supplied <paramref name="template"/> (the item's
    /// own frozen snapshot) rather than a hardcoded action→state map, so an
    /// in-flight item is judged by the rules it was submitted under. An item
    /// whose snapshot predates v8 has no <c>continue-review-during-*</c>
    /// transitions and yields <c>null</c> — callers then fall back to the
    /// pre-RA-372 behaviour rather than guessing.
    /// </summary>
    public static string? ResolveOriginatingStateId(WorkItem workItem, IWorkItemTemplate template)
    {
        var continueActionId = ResolveContinueActionId(workItem);
        if (continueActionId is null)
        {
            return null;
        }

        return template
            .Transitions.FirstOrDefault(t =>
                string.Equals(t.ActionId, continueActionId, StringComparison.OrdinalIgnoreCase)
            )
            ?.ToStateId;
    }
}
