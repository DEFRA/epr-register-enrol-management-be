using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-291 default <see cref="IReAccreditationQueryService"/>.
///
/// Deliberately thin: the state change itself goes through the framework
/// engine (<see cref="IWorkItemService.ApplyActionAsync(Guid, string, WorkItemActionWrite, ClaimsPrincipal, CancellationToken)"/>)
/// so state validation, the <c>action-applied</c> audit entry,
/// template-snapshot resolution and the post-action hooks (including the
/// Notify "Queried" email and the query push) all behave exactly as they do
/// for any other transition. The only bespoke part is (a) resolving which
/// <c>query-during-*</c> action applies to the item's current state, (b)
/// self-assigning the application to the querying case worker, (c) stamping
/// the open <see cref="Models.CurrentQuery"/> onto the payload so the hooks can
/// push it and put it in the operator's email, and (d) appending the RA-291
/// query detail — selected sections and free-text reason — to the audit log so
/// AC05 is satisfied and the application history renders what was asked for.
///
/// RA-000: (b), (c) and the transition are one write. They go to the engine
/// as a <see cref="WorkItemActionWrite"/>, which commits them in the
/// transition's single versioned save, together with the stamp's
/// <see cref="CurrentQuery.QueryId"/> on the transition's <c>action-applied</c>
/// entry so the hooks can tell this query's stamp from any other
/// (<see cref="ReAccreditationFreshQuery"/>). When two query requests for the
/// same item overlap, one commits and the other gets a concurrency conflict
/// or an invalid transition and writes nothing: no assignment, no stamp, no
/// email. A failed transition likewise leaves no assignment and no stamp
/// behind.
///
/// (d) follows as a separate append, so a failed transition leaves no query
/// recorded against the application; if the append fails after the
/// transition has committed, it is logged at Error rather than failing the
/// request.
///
/// The SLA clock is intentionally untouched: querying an application pauses
/// nobody's stopwatch.
/// </summary>
internal sealed class ReAccreditationQueryService(
    IWorkItemPersistence persistence,
    IWorkItemService engine,
    IWorkItemAuditAppender auditAppender,
    ILogger<ReAccreditationQueryService> logger,
    TimeProvider? timeProvider = null) : IReAccreditationQueryService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>Audit action id for the RA-291 query-detail entry.</summary>
    public const string AuditAction = "application-queried";

    public const string AuditActionDisplayName = "Application queried";

    /// <summary>
    /// <c>action-applied</c> detail carrying the <see cref="CurrentQuery.QueryId"/>
    /// the transition was applied for (RA-000).
    /// </summary>
    public const string QueryIdAuditDetail = "queryId";

    /// <summary>
    /// Payload field the open query is stored under. Matches the camelCase
    /// name the BSON convention gives <see cref="ReAccreditationPayload.CurrentQuery"/>.
    /// </summary>
    public const string CurrentQueryPayloadField = "currentQuery";

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
            // RA-527 follow-up: a started case ("Duly made") is queried as one
            // under assessment, so its response and Continue review follow
            // the assessment route back to 'assessment-in-progress'.
            ["assessment-started"] = "query-during-assessment",
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

        if (workItem is null)
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
        // assignment. The engine writes the normal `assigned` audit entry and
        // fires the RA-237 OfficerAssignment notification exactly as for a
        // manual assign. An item already assigned to this user is left as it
        // is (no audit entry, no duplicate email); an item held by someone
        // else is re-assigned to the querying regulator. RA-323: every
        // caseworker may assign to anyone, so this is not role-gated.
        var actorId = user.FindFirstValue("user:id");
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.MissingActorIdentity,
                "Querying a work item requires an authenticated end user; " +
                "the request did not include a 'user:id' claim.");
        }

        // RA-291: the open query is payload state the hooks read inside
        // ApplyActionAsync (the reason and sections they push and email).
        // RA-000: it is written in the transition's own versioned save, never
        // before it, so an overlapping request cannot replace it and a failed
        // transition cannot leave it behind. Setting only payload.currentQuery
        // on the loaded BSON payload leaves every other field exactly as
        // stored; round-tripping the payload through ReAccreditationPayload
        // instead would write modelled-but-absent fields as explicit nulls,
        // and an explicit payload.accreditationId: null enters the unique +
        // sparse index on that field (the RA-291 duplicate-key failure).
        var currentQuery = new CurrentQuery
        {
            QueryId = Guid.NewGuid().ToString(),
            Reason = reason,
            Sections = sections,
            RaisedAt = _timeProvider.GetUtcNow().UtcDateTime,
            RaisedBy = actorId,
        };

        var result = await engine.ApplyActionAsync(
            workItemId,
            actionId,
            new WorkItemActionWrite
            {
                AssignTo = new WorkItemAssignee(actorId, user.FindFirstValue("user:name")),
                // ToBsonDocument honours the registered camelCase convention, so
                // the stored shape matches what ReAccreditationPayload deserialises.
                PayloadFields = new Dictionary<string, BsonValue>
                {
                    [CurrentQueryPayloadField] = currentQuery.ToBsonDocument(),
                },
                ActionAppliedDetails = new Dictionary<string, string?>
                {
                    [QueryIdAuditDetail] = currentQuery.QueryId,
                },
            },
            user,
            cancellationToken);
        if (!result.IsSuccess)
        {
            logger.LogInformation(
                "Query of work item {WorkItemId} via {ActionId} not applied ({FailureCode}); nothing was written.",
                workItemId, actionId, result.FailureCode);
            return result;
        }

        var appended = await auditAppender.AppendAsync(
            workItemId,
            action: AuditAction,
            actionDisplayName: AuditActionDisplayName,
            details: new Dictionary<string, string?>
            {
                ["actionId"] = actionId,
                [QueryIdAuditDetail] = currentQuery.QueryId,
                ["sections"] = string.Join(",", sections),
                ["reason"] = reason,
            },
            user,
            // The transition is committed: a client that has stopped waiting
            // must not stop the audit record of why it happened being written.
            CancellationToken.None);

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
