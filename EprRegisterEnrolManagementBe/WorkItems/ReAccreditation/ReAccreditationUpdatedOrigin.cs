using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-337 / RA-372: shared resolution of "where did this work item come from
/// before it landed in <c>updated</c>, and where is it going back to".
///
/// The <c>updated</c> state is a waypoint. A queried application returns to it
/// when the operator responds, and it carries no identity of its own — every
/// question worth asking about an item sitting there ("what should a
/// caseworker be offered?", "which continue-review action moves it on?") is
/// really a question about the state the query was raised from.
///
/// That originating state is not stored on the document. It is derived from
/// the item's own audit history, which is deliberate: the audit log is the
/// record of what actually happened, so deriving from it cannot drift out of
/// sync with a denormalised copy, and it needs no migration to work on items
/// that are already in flight.
///
/// Two callers share this, and they must agree — if they disagreed, a
/// caseworker could be offered a call to action for one state and then be
/// moved into a different one:
/// <list type="bullet">
///   <item><see cref="ReAccreditationContinueReviewService"/>, to pick the
///   <c>continue-review-during-*</c> action that moves the item on.</item>
///   <item><see cref="ReAccreditationOriginStateResolver"/>, to report the
///   origin on the wire so the frontend can offer the matching call to
///   action.</item>
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
    /// Reads the most recent generic <c>action-applied</c> audit entry that
    /// actually moved the item <em>into</em> <c>updated</c>.
    ///
    /// The <c>toStateId</c> requirement is load-bearing, not belt-and-braces.
    /// Recency alone is not proof of causality: entries are ordered by
    /// <see cref="WorkItemAuditEntry.CreatedAt"/>, ties fall back to insertion
    /// order, and code outside the engine appends synthetic
    /// <c>action-applied</c> entries stamped with the current time — see
    /// <see cref="ReAccreditationDulyMadeSnapshotMigration"/>. Without this
    /// check, such an entry landing in the same tick as a genuine resume could
    /// win the sort and mis-derive the origin.
    ///
    /// A mis-derivation does not just pick the wrong
    /// <c>continue-review-during-*</c> action; it is also reported on the wire
    /// as the item's origin state, from which the frontend decides which call
    /// to action to offer — so a wrong answer could invite a caseworker to
    /// send an application backwards past assessment. Requiring the entry to
    /// name <c>updated</c> as its destination keeps the derivation tied to the
    /// event that actually put the item where it is.
    /// </summary>
    public static string? ResolveContinueActionId(WorkItem workItem)
    {
        var resumeActionId = workItem
            .AuditLog.Where(entry =>
                string.Equals(entry.Action, "action-applied", StringComparison.Ordinal)
                && string.Equals(
                    entry.Details.GetValueOrDefault("toStateId"),
                    StateId,
                    StringComparison.OrdinalIgnoreCase
                )
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
