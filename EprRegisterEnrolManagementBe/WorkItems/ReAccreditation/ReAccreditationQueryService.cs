using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using MongoDB.Bson;

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
/// to the item's current state, (b) self-assigning the application to the
/// querying case worker, (c) stamping the open
/// <see cref="Models.CurrentQuery"/> onto the payload so the notification hook
/// can put the reason in the operator's email, and (d) appending the RA-291
/// query detail — selected sections and free-text reason — to the audit log so
/// AC05 is satisfied and the application history renders what was asked for.
///
/// Write order is assign → stamp query → transition → audit. The stamp must
/// precede the transition because the notification hook runs inside
/// <see cref="IWorkItemService.ApplyActionAsync"/>; the audit entry follows it
/// so a failed transition leaves no query recorded against the application.
/// A successful assign is intentionally NOT compensated if a later step
/// fails: an assigned-but-un-queried item is the recoverable half-state
/// (a retry idempotently re-assigns and proceeds), whereas rolling back the
/// assign would need a further write that could itself fail.
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
        // by someone else is re-assigned to the querying regulator. RA-323:
        // every caseworker may assign to anyone, so this is not role-gated.
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

        // RA-291: stamp the open query onto the payload BEFORE the transition.
        // ReAccreditationNotificationHook runs as a post-action hook *inside*
        // ApplyActionAsync, so this is the only point at which the reason can
        // be made visible to the email it has to appear in. Writing it as
        // payload state (rather than threading it through the engine) keeps the
        // module concept out of the framework envelope and means the hook reads
        // the very same record the audit entry below is built from — so the
        // reason in the email is by construction the reason on the application.
        var stampFailure = await StampCurrentQueryAsync(
            workItemId, sections, reason, actorId, cancellationToken);
        if (stampFailure is not null)
        {
            return stampFailure;
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

    /// <summary>
    /// Write <see cref="CurrentQuery"/> onto the work item payload. Returns
    /// <c>null</c> on success, or the failure the caller should surface.
    ///
    /// Uses a targeted single-field write rather than load → mutate → replace.
    /// A full-payload replace round-trips the document through
    /// <see cref="ReAccreditationPayload"/>, which materialises modelled-but-
    /// absent fields as explicit nulls — and an explicit
    /// <c>payload.accreditationId: null</c> enters the unique + SPARSE index
    /// on that field, so the second query anywhere in the collection died with
    /// a duplicate-key error (RA-291). Setting only
    /// <c>payload.currentQuery</c> keeps every other field untouched by
    /// construction, for this field and any modelled field added later.
    /// </summary>
    private async Task<WorkItemActionResult?> StampCurrentQueryAsync(
        Guid workItemId,
        IReadOnlyList<string> sections,
        string reason,
        string actorId,
        CancellationToken cancellationToken)
    {
        var currentQuery = new CurrentQuery
        {
            Reason = reason,
            Sections = sections,
            RaisedAt = _timeProvider.GetUtcNow().UtcDateTime,
            RaisedBy = actorId,
        };

        // ToBsonDocument honours the registered camelCase convention, so the
        // stored shape matches what ReAccreditationPayload deserialises.
        var matched = await persistence.SetPayloadFieldAsync(
            workItemId,
            CurrentQueryPayloadField,
            currentQuery.ToBsonDocument(),
            cancellationToken);

        if (!matched)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'.");
        }

        return null;
    }
}
