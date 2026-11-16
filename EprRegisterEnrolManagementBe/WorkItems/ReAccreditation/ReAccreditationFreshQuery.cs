using System.Diagnostics.CodeAnalysis;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-000: checks that the <see cref="CurrentQuery"/> on a work item is
/// complete and is the one stamped for the <c>query-during-*</c> transition a
/// post-action hook is now handling, so the hook never acts on a missing
/// query, the previous query's leftovers, or another request's query.
///
/// <see cref="CurrentQuery"/> is never cleared: it stays on the payload after
/// the operator responds and the item resumes, and the next query overwrites
/// it. A <c>query-during-*</c> transition applied without
/// <see cref="ReAccreditationQueryService"/> would therefore see either
/// nothing (first query) or the previous query (every later one).
///
/// The query service writes the stamp and the transition in one versioned
/// save, and records the stamp's <see cref="CurrentQuery.QueryId"/> on the
/// transition's own <c>action-applied</c> entry
/// (<see cref="ReAccreditationQueryService.QueryIdAuditDetail"/>). The check
/// is that the two ids match. It compares no timestamps, so clock skew
/// between pods cannot fail a genuine query, and a stamp from a different
/// request — an earlier query, or one that lost a race — carries a different
/// id. A transition with no <c>queryId</c> was not applied by the query
/// service, so it is rejected too.
/// </summary>
internal static class ReAccreditationFreshQuery
{
    private const string ActionAppliedAuditAction = "action-applied";
    private const string QueriedStateId = "queried";

    /// <summary>
    /// The <see cref="CurrentQuery"/> stamped for the transition being
    /// handled, with every field a consumer reads guaranteed present.
    /// </summary>
    internal sealed record VerifiedQuery(string Reason, IReadOnlyList<string> Sections, DateTime RaisedAt);

    /// <summary>
    /// Check that <paramref name="currentQuery"/> is the query stamped for the
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

        var transitionQueryId = workItem.AuditLog[transitionIndex].Details
            .GetValueOrDefault(ReAccreditationQueryService.QueryIdAuditDetail);
        if (string.IsNullOrEmpty(transitionQueryId))
        {
            return $"the {actionId} transition's action-applied entry records no "
                + $"{ReAccreditationQueryService.QueryIdAuditDetail}, so it was not applied by the query service";
        }

        if (string.IsNullOrEmpty(currentQuery.QueryId))
        {
            return "payload.currentQuery.queryId is missing, so it belongs to an earlier query";
        }

        if (!string.Equals(currentQuery.QueryId, transitionQueryId, StringComparison.Ordinal))
        {
            return $"payload.currentQuery is query {currentQuery.QueryId}, but the {actionId} transition "
                + $"was applied for query {transitionQueryId}";
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

}
