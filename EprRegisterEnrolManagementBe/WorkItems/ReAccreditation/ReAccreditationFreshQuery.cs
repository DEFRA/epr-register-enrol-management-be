using System.Globalization;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-000: proves that the <see cref="CurrentQuery"/> on a work item was
/// stamped by the query operation whose <c>query-during-*</c> transition a
/// post-action hook is now handling, so the hook never acts on a missing
/// query or on the previous query's leftovers.
///
/// <see cref="CurrentQuery"/> is never cleared: it stays on the payload after
/// the operator responds and the item resumes, and the next query overwrites
/// it. A <c>query-during-*</c> transition applied without
/// <see cref="ReAccreditationQueryService"/> stamping first would therefore
/// see either nothing (first query) or the previous query (every later one).
///
/// Freshness is proven from the work item's own audit log, not from extra
/// state: the query service stamps <see cref="CurrentQuery.RaisedAt"/>
/// immediately before it applies the transition, so a fresh stamp lies
/// between the transition that brought the item into its from-state (the
/// <c>action-applied</c> entry before this one, or
/// <see cref="WorkItem.SubmittedAt"/> when there is none) and this
/// transition's own <c>action-applied</c> entry. A stale stamp is always
/// older than that lower bound, because leaving <c>queried</c> again is
/// itself an engine transition (a <c>resume-during-*</c>) that writes an
/// <c>action-applied</c> entry. No new field, write or data migration is
/// needed, and items already carrying a stale stamp are caught as-is.
/// </summary>
internal static class ReAccreditationFreshQuery
{
    private const string ActionAppliedAuditAction = "action-applied";
    private const string QueriedStateId = "queried";

    /// <summary>
    /// The fresh query, or the reason one could not be proven. Exactly one of
    /// <see cref="Query"/> and <see cref="FailureReason"/> is set.
    /// </summary>
    internal sealed record Result(CurrentQuery? Query, string? FailureReason)
    {
        public bool IsFresh => Query is not null;
    }

    /// <summary>
    /// Verify that <paramref name="currentQuery"/> was stamped for the
    /// <paramref name="actionId"/> transition out of
    /// <paramref name="fromStateId"/> that has just been applied to
    /// <paramref name="workItem"/>, and that it carries every field a
    /// consumer reads (<see cref="CurrentQuery.RaisedAt"/>, a non-empty
    /// <see cref="CurrentQuery.Sections"/> and a non-null
    /// <see cref="CurrentQuery.Reason"/>; RA-534 made the reason optional, and
    /// the query service stamps an omitted one as an empty string, so empty is
    /// valid but absent is not).
    /// </summary>
    public static Result Verify(
        WorkItem workItem,
        CurrentQuery? currentQuery,
        string actionId,
        string fromStateId)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        if (currentQuery is null)
        {
            return Fail("payload.currentQuery is missing");
        }

        if (currentQuery.RaisedAt is not { } raisedAt)
        {
            return Fail("payload.currentQuery.raisedAt is missing");
        }

        if (currentQuery.Sections is not { Count: > 0 })
        {
            return Fail("payload.currentQuery.sections is missing or empty");
        }

        if (currentQuery.Reason is null)
        {
            return Fail("payload.currentQuery.reason is missing");
        }

        // The engine appends this transition's action-applied entry before it
        // persists and before it runs the post-action hooks, so it is the last
        // action-applied entry on the instance the hook is handed.
        var transitionIndex = workItem.AuditLog.FindLastIndex(IsActionApplied);
        if (transitionIndex < 0
            || !IsEntryFor(workItem.AuditLog[transitionIndex], actionId, fromStateId))
        {
            return Fail(
                $"the audit log has no action-applied entry for {actionId} from '{fromStateId}' to '{QueriedStateId}'");
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
            return Fail(
                $"payload.currentQuery was raised at {Format(raisedAt)}, before the work item entered "
                + $"'{fromStateId}' at {Format(enteredFromStateAt)}, so it belongs to an earlier query");
        }

        if (raisedAt > transitionAt)
        {
            return Fail(
                $"payload.currentQuery was raised at {Format(raisedAt)}, after the {actionId} transition "
                + $"at {Format(transitionAt)}");
        }

        return new Result(currentQuery, null);
    }

    private static Result Fail(string reason) => new(null, reason);

    private static bool IsActionApplied(WorkItemAuditEntry entry) =>
        string.Equals(entry.Action, ActionAppliedAuditAction, StringComparison.Ordinal);

    private static bool IsEntryFor(WorkItemAuditEntry entry, string actionId, string fromStateId) =>
        string.Equals(entry.Details.GetValueOrDefault("actionId"), actionId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(entry.Details.GetValueOrDefault("fromStateId"), fromStateId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(entry.Details.GetValueOrDefault("toStateId"), QueriedStateId, StringComparison.OrdinalIgnoreCase);

    private static string Format(DateTime value) => value.ToString("O", CultureInfo.InvariantCulture);
}
