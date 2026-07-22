using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-311/MBE-1 default <see cref="IReAccreditationResumeService"/>.
///
/// Deliberately thin, mirroring <see cref="ReAccreditationQueryService"/>:
/// the state change itself goes through the framework engine
/// (<see cref="IWorkItemService.ApplyActionAsync"/>) so state validation,
/// the generic <c>action-applied</c> audit entry, and post-action hooks all
/// behave exactly as they do for any other transition. The bespoke parts are
/// (a) resolving which <c>resume-during-*</c> action applies — the inverse
/// of the lookup <see cref="ReAccreditationQueryService"/> performs, read
/// off the work item's own <c>application-queried</c> audit history rather
/// than a static from-state map, since the "from" state here is always
/// <c>queried</c> — (b) stamping the resubmitted section values and file
/// references onto the payload so they are captured before the transition,
/// and (c) appending the <c>query-responded</c> audit entry (AC07/AC08).
///
/// Write order mirrors <see cref="ReAccreditationQueryService"/>: patch the
/// payload field before the transition (so a future notification/push hook
/// reading it inside <see cref="IWorkItemService.ApplyActionAsync"/> would
/// see it), transition, then audit.
/// </summary>
internal sealed class ReAccreditationResumeService(
    IWorkItemPersistence persistence,
    IWorkItemService engine,
    IWorkItemAuditAppender auditAppender,
    ILogger<ReAccreditationResumeService> logger,
    TimeProvider? timeProvider = null) : IReAccreditationResumeService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public const string AuditAction = "query-responded";
    public const string AuditActionDisplayName = "Query responded";
    public const string LatestSectionsPayloadField = "latestSections";

    /// <summary>
    /// States a work item can validly be resumed into. Used to tell a
    /// genuine "already resumed" idempotent replay apart from a work item
    /// that has moved on to some other, unrelated state (which is a real
    /// conflict, not a replay).
    /// </summary>
    private static readonly IReadOnlySet<string> s_resumeTargetStates =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "submitted", "duly-made", "assessment-in-progress", "awaiting-decision",
        };

    /// <summary>
    /// Inverse of <see cref="ReAccreditationQueryService"/>'s
    /// state→query-action map: which <c>resume-during-*</c> action
    /// corresponds to the <c>query-during-*</c> action that put the work
    /// item into <c>queried</c> in the first place.
    /// </summary>
    private static readonly Dictionary<string, string> s_resumeActionByQueryAction =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["query-during-duly-making"] = "resume-during-duly-making",
            ["query-during-duly-made"] = "resume-during-duly-made",
            ["query-during-assessment"] = "resume-during-assessment",
            ["query-during-decision"] = "resume-during-decision",
        };

    public async Task<WorkItemActionResult> ResumeFromQueryAsync(
        Guid workItemId,
        ResumeFromQueryRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
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

        if (!string.Equals(workItem.StateId, "queried", StringComparison.OrdinalIgnoreCase))
        {
            // A genuinely concurrent/duplicate resubmit (e.g. a double-click)
            // must not fail the caller's retry — once the work item has left
            // 'queried' into a state resume-from-query could have put it in,
            // treat this as a no-op success rather than a conflict. Anything
            // else (approved, rejected, withdrawn, ...) is a real conflict:
            // this work item was never waiting on this call.
            if (s_resumeTargetStates.Contains(workItem.StateId))
            {
                logger.LogInformation(
                    "Resume-from-query for work item {WorkItemId} is a no-op: already in state '{StateId}'.",
                    workItemId, workItem.StateId);
                return WorkItemActionResult.IdempotentReplay(workItem);
            }

            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item {workItemId} is in state '{workItem.StateId}' and cannot be resumed from a query.");
        }

        var queryActionId = workItem
            .AuditLog
            .Where(entry => string.Equals(entry.Action, ReAccreditationQueryService.AuditAction, StringComparison.Ordinal))
            .OrderByDescending(entry => entry.CreatedAt)
            .Select(entry => entry.Details.GetValueOrDefault("actionId"))
            .FirstOrDefault(actionId => actionId is not null);

        if (queryActionId is null
            || !s_resumeActionByQueryAction.TryGetValue(queryActionId, out var resumeActionId))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item {workItemId} is 'queried' but its query action could not be resolved " +
                "from its audit history, so the matching resume action cannot be determined.");
        }

        var stampFailure = await StampLatestSectionsAsync(workItemId, request, user, cancellationToken);
        if (stampFailure is not null)
        {
            return stampFailure;
        }

        var result = await engine.ApplyActionAsync(workItemId, resumeActionId, user, cancellationToken);
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
                ["actionId"] = resumeActionId,
                ["sectionKeys"] = string.Join(",", request.SectionKeys ?? []),
                ["responderFullName"] = request.ResponderContactDetails?.FullName,
                ["responderEmail"] = request.ResponderContactDetails?.Email,
                ["responderRole"] = request.ResponderContactDetails?.Role,
                ["fileReferences"] = SerialiseFileReferences(request.FileReferences),
            },
            user,
            cancellationToken);

        if (!appended)
        {
            // The transition itself is already persisted, so failing the
            // request now would misreport the application's state. Log
            // loudly instead — the generic action-applied entry still
            // records that a resume happened, only the detail is missing.
            logger.LogError(
                "Query-responded audit entry could not be appended to work item {WorkItemId} " +
                "after action {ActionId} was applied.",
                workItemId, resumeActionId);
        }

        logger.LogInformation(
            "Re-accreditation work item {WorkItemId} resumed from query by {UserId} via {ActionId} " +
            "against {SectionCount} section(s)",
            workItemId, user.FindFirstValue("user:id"), resumeActionId, request.SectionKeys?.Count ?? 0);

        // Re-read so the response carries the query-responded audit entry
        // the out-of-band appender wrote against its own copy of the document.
        var refreshed = await persistence.GetByIdAsync(workItemId, cancellationToken);
        return refreshed is null ? result : WorkItemActionResult.Success(refreshed);
    }

    /// <summary>
    /// Write the resubmitted section values and file references onto
    /// <c>payload.latestSections</c> BEFORE the transition — same targeted
    /// single-field-write convention as
    /// <see cref="ReAccreditationQueryService"/>'s <c>currentQuery</c> stamp,
    /// for the same reason (a full-payload replace would materialise
    /// modelled-but-absent fields as explicit nulls).
    /// </summary>
    private async Task<WorkItemActionResult?> StampLatestSectionsAsync(
        Guid workItemId,
        ResumeFromQueryRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var sectionsDoc = new BsonDocument();
        if (request.Sections is not null)
        {
            foreach (var (sectionKey, value) in request.Sections)
            {
                sectionsDoc[sectionKey] = WorkItemPayloadConverter.ToBson(value);
            }
        }

        var fileReferencesArray = new BsonArray(
            (request.FileReferences ?? []).Select(f => new BsonDocument
            {
                ["sectionKey"] = ToBsonValue(f.SectionKey),
                ["fileId"] = ToBsonValue(f.FileId),
                ["filename"] = ToBsonValue(f.Filename),
                ["s3Key"] = ToBsonValue(f.S3Key),
            }));

        var latestSections = new BsonDocument
        {
            ["sectionKeys"] = new BsonArray((request.SectionKeys ?? []).Select(s => (BsonValue)s)),
            ["sections"] = sectionsDoc,
            ["fileReferences"] = fileReferencesArray,
            ["respondedAt"] = _timeProvider.GetUtcNow().UtcDateTime,
            ["respondedBy"] = ToBsonValue(user.FindFirstValue("user:id")),
        };

        var matched = await persistence.SetPayloadFieldAsync(
            workItemId, LatestSectionsPayloadField, latestSections, cancellationToken);

        if (!matched)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'.");
        }

        return null;
    }

    private static BsonValue ToBsonValue(string? value) => value is null ? BsonNull.Value : new BsonString(value);

    private static string SerialiseFileReferences(IReadOnlyList<SectionFileReference>? fileReferences) =>
        fileReferences is null || fileReferences.Count == 0
            ? string.Empty
            : string.Join(";", fileReferences.Select(f => $"{f.SectionKey}:{f.FileId}:{f.Filename}"));
}
