using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;

/// <summary>
/// RA-698: <c>POST /work-items/{id}/query</c> body for an add-ORS item. The
/// item is one ORS, so the query is implicitly a combined ORS and BES query:
/// the body is the reason only. <see cref="Sections"/> exists only so a
/// request that sends section keys is refused rather than silently ignored.
/// </summary>
internal sealed record AddOverseasSiteQueryRequest(string? Reason, IReadOnlyList<string>? Sections = null);

/// <summary>
/// RA-698: request validation for the add-ORS query endpoint, generalised from
/// re-accreditation's query validator. Returns the human-readable failure
/// detail, or <c>null</c> when the request is valid. Unlike re-accreditation's
/// (RA-534), the reason is mandatory: it is the whole query, and it is what
/// the operator is shown.
/// </summary>
internal static class AddOverseasSiteQueryValidator
{
    /// <summary>The word cap, shared with the case management frontend and re-accreditation's query.</summary>
    public const int MaxReasonWords = 200;

    public const string NoReasonMessage = "Enter your query";
    public const string ReasonTooLongMessage = "Query must be 200 words or fewer";
    public const string SectionsNotAcceptedMessage =
        "An overseas reprocessing site query takes a reason only: section keys are not accepted";

    public static string? Validate(AddOverseasSiteQueryRequest? request)
    {
        if (request?.Sections is not null)
        {
            return SectionsNotAcceptedMessage;
        }

        if (string.IsNullOrWhiteSpace(request?.Reason))
        {
            return NoReasonMessage;
        }

        return CountWords(request.Reason) > MaxReasonWords ? ReasonTooLongMessage : null;
    }

    /// <summary>Trim, split on any run of whitespace, count the non-empty tokens (the frontend's rule).</summary>
    internal static int CountWords(string reason) =>
        reason.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

/// <summary>
/// RA-698: raises a query against an add-ORS work item. A module-scoped
/// service, like re-accreditation's.
/// </summary>
internal interface IAddOverseasSiteQueryService
{
    /// <summary>
    /// Assign the item to the acting user, move it into <c>queried</c> and
    /// record the reason on the audit log. The caller never supplies an action
    /// id: the <c>query-during-*</c> transition is resolved from the item's
    /// current state. A state with no query transition (already
    /// <c>queried</c>, or decided) fails with
    /// <see cref="WorkItemActionFailureCode.InvalidTransition"/>.
    /// </summary>
    Task<WorkItemActionResult> QueryAsync(
        Guid workItemId,
        string reason,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// RA-698 default <see cref="IAddOverseasSiteQueryService"/>, generalised from
/// <c>ReAccreditationQueryService</c> with the same write order: assign, stamp
/// the open query on the payload, transition, audit. The stamp precedes the
/// transition because the query push hook
/// (<see cref="AddOverseasSiteQueryPushHook"/>) runs inside
/// <see cref="IWorkItemService.ApplyActionAsync"/> and reads the note from it;
/// the audit entry follows it so a failed transition records no query. The
/// stamp is a single-field write (never a whole-payload replace), as there.
/// The SLA clock is untouched.
/// </summary>
internal sealed class AddOverseasSiteQueryService(
    IWorkItemPersistence persistence,
    IWorkItemService engine,
    IWorkItemAuditAppender auditAppender,
    ILogger<AddOverseasSiteQueryService> logger,
    TimeProvider? timeProvider = null
) : IAddOverseasSiteQueryService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// The audit action id for the query-detail entry. The same id as
    /// re-accreditation's, so the application history renders it alike.
    /// </summary>
    public const string AuditAction = "application-queried";

    public const string AuditActionDisplayName = "Application queried";

    /// <summary>The payload field the open query is stored under.</summary>
    public const string CurrentQueryPayloadField = "currentQuery";

    public async Task<WorkItemActionResult> QueryAsync(
        Guid workItemId,
        string reason,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(user);

        var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."
            );
        }

        if (!string.Equals(workItem.TypeId, AddOverseasSiteType.Id, StringComparison.OrdinalIgnoreCase))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                $"Work item {workItemId} is of type '{workItem.TypeId}', not '{AddOverseasSiteType.Id}'."
            );
        }

        var actionId = StandardQueryActions.ResolveQueryActionId(workItem.StateId);
        if (actionId is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item {workItemId} is in state '{workItem.StateId}' and cannot be queried. "
                    + "An overseas reprocessing site can only be queried while it is submitted, duly made, "
                    + "under assessment, or awaiting a decision."
            );
        }

        var actorId = user.FindFirstValue("user:id");
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.MissingActorIdentity,
                "Querying a work item requires an authenticated end user; the request did not include a 'user:id' claim."
            );
        }

        // As RA-291: the query page promises the item will be assigned to the
        // querier, and a failed assign must leave it un-queried rather than
        // queried-but-unassigned. An assign that succeeds before a later
        // failure is not compensated: a retry re-assigns idempotently.
        var assignResult = await engine.AssignAsync(
            workItemId,
            actorId,
            user.FindFirstValue("user:name"),
            user,
            cancellationToken
        );
        if (!assignResult.IsSuccess)
        {
            logger.LogInformation(
                "Query of work item {WorkItemId} abandoned: self-assignment to {UserId} failed ({FailureCode}).",
                workItemId,
                actorId,
                assignResult.FailureCode
            );
            return assignResult;
        }

        var matched = await persistence.SetPayloadFieldAsync(
            workItemId,
            CurrentQueryPayloadField,
            new BsonDocument
            {
                ["reason"] = reason,
                ["raisedAt"] = _timeProvider.GetUtcNow().UtcDateTime,
                ["raisedBy"] = actorId,
            },
            cancellationToken
        );
        if (!matched)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."
            );
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
            details: new Dictionary<string, string?> { ["actionId"] = actionId, ["reason"] = reason },
            user,
            // The transition is committed: a client that has stopped waiting
            // must not stop the audit record of why it happened being written.
            CancellationToken.None
        );
        if (!appended)
        {
            logger.LogError(
                "Query detail audit entry could not be appended to work item {WorkItemId} after action {ActionId} was applied.",
                workItemId,
                actionId
            );
        }

        logger.LogInformation(
            "Add-ORS work item {WorkItemId} queried by {UserId} via {ActionId}",
            workItemId,
            actorId,
            actionId
        );

        var refreshed = await persistence.GetByIdAsync(workItemId, cancellationToken);
        return refreshed is null ? result : WorkItemActionResult.Success(refreshed);
    }
}
