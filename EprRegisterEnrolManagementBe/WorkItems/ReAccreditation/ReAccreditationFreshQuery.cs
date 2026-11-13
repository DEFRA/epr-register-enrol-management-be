using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-000: checks that the <see cref="CurrentQuery"/> on a work item is
/// complete and was stamped inside the time window of the <c>query-during-*</c>
/// transition a post-action hook is now handling, so the hook never acts on a
/// missing query or on the previous query's leftovers.
///
/// <see cref="CurrentQuery"/> is never cleared: it stays on the payload after
/// the operator responds and the item resumes, and the next query overwrites
/// it. A <c>query-during-*</c> transition applied without
/// <see cref="ReAccreditationQueryService"/> stamping first would therefore
/// see either nothing (first query) or the previous query (every later one).
///
/// The window comes from the work item's own audit log, not from extra state:
/// the query service stamps <see cref="CurrentQuery.RaisedAt"/> immediately
/// before it applies the transition, so a stamp written for this transition
/// lies between the transition that brought the item into its from-state (the
/// <c>action-applied</c> entry before this one, or
/// <see cref="WorkItem.SubmittedAt"/> when there is none) and this
/// transition's own <c>action-applied</c> entry. A stale stamp is always
/// older than that lower bound, because leaving <c>queried</c> again is
/// itself an engine transition (a <c>resume-during-*</c>) that writes an
/// <c>action-applied</c> entry. No new field, write or data migration is
/// needed, and items already carrying a stale stamp are caught as-is.
///
/// A time window is weaker than proof that the stamp was written by the
/// request whose transition is being handled. Two known gaps:
/// <list type="bullet">
/// <item><description>Overlapping queries. The stamp is a separate, unversioned
/// payload write that does not check the item's state, and the transition is
/// applied after it. When two query requests for the same item overlap, both
/// stamps can land inside the window; one transition wins, and its hooks push
/// and email whichever reason and sections were written last, which may be
/// the losing request's. The race predates RA-000; this check does not close
/// it.</description></item>
/// <item><description>Clock skew. The lower bound is the previous
/// <c>action-applied</c> entry's <c>CreatedAt</c>, which another pod may have
/// written. If that pod's clock runs ahead of the one stamping the query, a
/// genuine query fails the check: the item still ends up <c>queried</c>, but
/// nothing is pushed or emailed, and only the Error log and the
/// <c>query-push-failed</c> / <c>notification-failed</c> entries record
/// it.</description></item>
/// </list>
/// </summary>
internal static class ReAccreditationFreshQuery
{
    private const string ActionAppliedAuditAction = "action-applied";
    private const string QueriedStateId = "queried";

    /// <summary>
    /// A <see cref="CurrentQuery"/> stamped inside the window of the transition
    /// being handled, with every field a consumer reads guaranteed present.
    /// </summary>
    internal sealed record VerifiedQuery(string Reason, IReadOnlyList<string> Sections, DateTime RaisedAt);

    /// <summary>
    /// Check that <paramref name="currentQuery"/> was stamped inside the window
    /// of the <paramref name="actionId"/> transition out of
    /// <paramref name="fromStateId"/> that has just been applied to
    /// <paramref name="workItem"/> (see the class remarks for what the window
    /// does not rule out), and that it carries every field a
    /// consumer reads (<see cref="CurrentQuery.RaisedAt"/>, a non-empty
    /// <see cref="CurrentQuery.Sections"/> and a non-null
    /// <see cref="CurrentQuery.Reason"/>; RA-534 made the reason optional, and
    /// the query service stamps an omitted one as an empty string, so empty is
    /// valid but absent is not).
    /// </summary>
    public static bool TryVerify(
        WorkItem workItem,
        CurrentQuery? currentQuery,
        string actionId,
        string fromStateId,
        [NotNullWhen(true)] out VerifiedQuery? query,
        [NotNullWhen(false)] out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        failureReason = Check(workItem, currentQuery, actionId, fromStateId, out query);
        return failureReason is null;
    }

    /// <summary>The failure reason, or <c>null</c> with <paramref name="query"/> set.</summary>
    private static string? Check(
        WorkItem workItem,
        CurrentQuery? currentQuery,
        string actionId,
        string fromStateId,
        out VerifiedQuery? query)
    {
        query = null;

        if (currentQuery is null)
        {
            return "payload.currentQuery is missing";
        }

        if (currentQuery.RaisedAt is not { } raisedAt)
        {
            return "payload.currentQuery.raisedAt is missing";
        }

        if (currentQuery.Sections is not { Count: > 0 } sections)
        {
            return "payload.currentQuery.sections is missing or empty";
        }

        if (currentQuery.Reason is not { } reason)
        {
            return "payload.currentQuery.reason is missing";
        }

        // The engine appends this transition's action-applied entry before it
        // persists and before it runs the post-action hooks, so it is the last
        // action-applied entry on the instance the hook is handed.
        var transitionIndex = workItem.AuditLog.FindLastIndex(IsActionApplied);
        if (transitionIndex < 0
            || !IsEntryFor(workItem.AuditLog[transitionIndex], actionId, fromStateId))
        {
            return $"the audit log has no action-applied entry for {actionId} from '{fromStateId}' to '{QueriedStateId}'";
        }

        var transitionAt = workItem.AuditLog[transitionIndex].CreatedAt;
        var previousIndex = transitionIndex == 0
            ? -1
            : workItem.AuditLog.FindLastIndex(transitionIndex - 1, transitionIndex, IsActionApplied);
        var enteredFromStateAt = previousIndex < 0
            ? workItem.SubmittedAt
            : workItem.AuditLog[previousIndex].CreatedAt;

        if (raisedAt < enteredFromStateAt)
        {
            return $"payload.currentQuery was raised at {Format(raisedAt)}, before the work item entered "
                + $"'{fromStateId}' at {Format(enteredFromStateAt)}, so it belongs to an earlier query";
        }

        if (raisedAt > transitionAt)
        {
            return $"payload.currentQuery was raised at {Format(raisedAt)}, after the {actionId} transition "
                + $"at {Format(transitionAt)}";
        }

        query = new VerifiedQuery(reason, sections, raisedAt);
        return null;
    }

    private static bool IsActionApplied(WorkItemAuditEntry entry) =>
        string.Equals(entry.Action, ActionAppliedAuditAction, StringComparison.Ordinal);

    private static bool IsEntryFor(WorkItemAuditEntry entry, string actionId, string fromStateId) =>
        string.Equals(entry.Details.GetValueOrDefault("actionId"), actionId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(entry.Details.GetValueOrDefault("fromStateId"), fromStateId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(entry.Details.GetValueOrDefault("toStateId"), QueriedStateId, StringComparison.OrdinalIgnoreCase);

    private static string Format(DateTime value) => value.ToString("O", CultureInfo.InvariantCulture);
}
