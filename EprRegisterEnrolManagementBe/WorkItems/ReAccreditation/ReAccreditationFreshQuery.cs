using System.Diagnostics.CodeAnalysis;
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
    /// A <see cref="CurrentQuery"/> proven to belong to the transition being
    /// handled, with every field a consumer reads guaranteed present.
    /// </summary>
    internal sealed record VerifiedQuery(string Reason, IReadOnlyList<string> Sections, DateTime RaisedAt);

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
