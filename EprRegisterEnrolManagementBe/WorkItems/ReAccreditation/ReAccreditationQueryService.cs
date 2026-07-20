using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-291 default <see cref="IReAccreditationQueryService"/>.
///
/// Deliberately thin: the state change itself goes through the framework
/// engine (<see cref="IWorkItemService.ApplyActionAsync"/>) so state
/// validation, the <c>action-applied</c> audit entry, template-snapshot
/// resolution and the post-action hooks (including the Notify "Queried"
/// email) all behave exactly as they do for any other transition. The only
/// bespoke part is (a) resolving which <c>query-during-*</c> action applies
/// to the item's current state, and (b) appending the RA-291 query detail —
/// selected sections and free-text reason — to the audit log so AC05 is
/// satisfied and the application history renders what was asked for.
///
/// The SLA clock is intentionally untouched: querying an application pauses
/// nobody's stopwatch.
/// </summary>
internal sealed class ReAccreditationQueryService(
    IWorkItemPersistence persistence,
    IWorkItemService engine,
    IWorkItemAuditAppender auditAppender,
    ILogger<ReAccreditationQueryService> logger) : IReAccreditationQueryService
{
    /// <summary>Audit action id for the RA-291 query-detail entry.</summary>
    public const string AuditAction = "application-queried";

    public const string AuditActionDisplayName = "Application queried";

    /// <summary>
    /// Which query transition applies in which state. Kept next to the
    /// transitions declared on <see cref="ReAccreditationType"/> in spirit:
    /// every non-terminal pre-decision state has exactly one way out to
    /// <c>queried</c>, and <c>queried</c> itself has none — an application
    /// cannot be queried twice.
    /// </summary>
    private static readonly Dictionary<string, string> s_queryActionByState =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["submitted"] = "query-during-duly-making",
            ["duly-made"] = "query-during-duly-made",
            ["assessment-in-progress"] = "query-during-assessment",
            ["awaiting-decision"] = "query-during-decision",
        };

    /// <summary>
    /// Resolve the <c>query-during-*</c> action id for a state, or
    /// <c>null</c> when the state cannot be queried.
    /// </summary>
    public static string? ResolveQueryActionId(string? stateId) =>
        stateId is not null && s_queryActionByState.TryGetValue(stateId, out var actionId)
            ? actionId
            : null;

    public async Task<WorkItemActionResult> QueryAsync(
        Guid workItemId,
        IReadOnlyList<string> sections,
        string reason,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(user);

        var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);

        // Cross-tenant gate (epr-946 contract): hide existence from callers
        // that cannot read the document.
        if (workItem is null || !WorkItemTenancy.CanRead(user, workItem))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'.");
        }

        if (!string.Equals(workItem.TypeId, ReAccreditationType.Id, StringComparison.OrdinalIgnoreCase))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                $"Work item {workItemId} is of type '{workItem.TypeId}', not '{ReAccreditationType.Id}'.");
        }

        var actionId = ResolveQueryActionId(workItem.StateId);
        if (actionId is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item {workItemId} is in state '{workItem.StateId}' and cannot be queried. " +
                "An application can only be queried while it is submitted, duly made, " +
                "under assessment, or awaiting a decision.");
        }

        // RA-291: the query page tells the case worker "the application will
        // also be assigned to you", so the query operation owns that
        // assignment. Done BEFORE the transition: if the assign fails the
        // application stays un-queried rather than ending up
        // queried-but-unassigned, which is the harder state to recover from.
        //
        // Routed through the engine rather than written straight onto the
        // document so the normal `assigned` audit entry and the RA-237
        // OfficerAssignment notification fire exactly as they do for a manual
        // assign. Re-assigning to the same user is an idempotent replay in the
        // engine (success, no audit entry, no duplicate email); an item held
        // by someone else is re-assigned to the querying regulator, which the
        // engine permits only for callers holding the `assign` role.
        var actorId = user.FindFirstValue("user:id");
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.MissingActorIdentity,
                "Querying a work item requires an authenticated end user; " +
                "the request did not include a 'user:id' claim.");
        }

        var assignResult = await engine.AssignAsync(
            workItemId, actorId, user.FindFirstValue("user:name"), user, cancellationToken);
        if (!assignResult.IsSuccess)
        {
            logger.LogInformation(
                "Query of work item {WorkItemId} abandoned: self-assignment to {UserId} failed ({FailureCode}).",
                workItemId, actorId, assignResult.FailureCode);
            return assignResult;
        }

        var result = await engine.ApplyActionAsync(workItemId, actionId, user, cancellationToken);
        if (!result.IsSuccess)
        {
            return result;
        }

        var appended = await auditAppender.AppendAsync(
            workItemId,
            action: AuditAction,
            actionDisplayName: AuditActionDisplayName,
            details: new Dictionary<string, string?>
            {
                ["actionId"] = actionId,
                ["sections"] = string.Join(",", sections),
                ["reason"] = reason,
            },
            user,
            cancellationToken);

        if (!appended)
        {
            // The transition itself is already persisted, so failing the
            // request now would misreport the application's state. Log
            // loudly instead — the generic action-applied entry still
            // records that a query happened, only the detail is missing.
            logger.LogError(
                "Query detail audit entry could not be appended to work item {WorkItemId} " +
                "after action {ActionId} was applied.",
                workItemId, actionId);
        }

        logger.LogInformation(
            "Re-accreditation work item {WorkItemId} queried by {UserId} via {ActionId} " +
            "against {SectionCount} section(s)",
            workItemId, user.FindFirstValue("user:id"), actionId, sections.Count);

        // Re-read so the response carries the query-detail audit entry the
        // out-of-band appender wrote against its own copy of the document.
        var refreshed = await persistence.GetByIdAsync(workItemId, cancellationToken);
        return refreshed is null ? result : WorkItemActionResult.Success(refreshed);
    }
}
