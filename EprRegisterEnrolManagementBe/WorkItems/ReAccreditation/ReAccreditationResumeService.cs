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
/// (c) appending the <c>query-responded</c> audit entry (AC07/AC08), and
/// (d) RA-523: restoring the querying case worker's assignment when the
/// application comes back owned by nobody — see
/// <see cref="RestoreQuerierAssignmentAsync"/>.
///
/// Write order mirrors <see cref="ReAccreditationQueryService"/>: patch the
/// payload field before the transition (so a future notification/push hook
/// reading it inside <see cref="IWorkItemService.ApplyActionAsync"/> would
/// see it), transition, then audit. The RA-523 assignment restore comes last,
/// deliberately: unlike the query's self-assign it is a consequence of a
/// completed resume, so it must never leave an assigned-but-un-resumed item
/// behind, and its failure must never fail the operator's resubmission.
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
    /// RA-523: how many times the post-resume assignment restore will retry a
    /// concurrency conflict before giving up. See
    /// <see cref="RestoreQuerierAssignmentAsync"/>.
    /// </summary>
    private const int MaxAssignAttempts = 4;

    /// <summary>Base backoff between assignment-restore attempts; scaled by attempt number.</summary>
    private static readonly TimeSpan s_assignRetryDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// States a work item can validly be resumed into. Used to tell a
    /// genuine "already resumed" idempotent replay apart from a work item
    /// that has moved on to some other, unrelated state (which is a real
    /// conflict, not a replay).
    ///
    /// RA-337: resume-during-* now lands on the single 'updated' waypoint
    /// state rather than jumping straight back to the originating state, so
    /// 'updated' is the only valid resume target — even though a caseworker
    /// may since have moved the item on further via continue-review-during-*,
    /// that is a distinct, later step this service has no opinion on.
    /// </summary>
    private static readonly IReadOnlySet<string> s_resumeTargetStates =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "updated" };

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

    /// <summary>
    /// RA-291/RA-311: the canonical top-level payload field each resubmittable
    /// section's value must also be merged into, so readers of the payload
    /// (e.g. the case management summary page) see the resubmitted values
    /// without having to know about <see cref="LatestSectionsPayloadField"/>.
    ///
    /// Keyed by the operator backend's <c>OperatorSection</c> enum name
    /// (<c>HttpCaseWorkingApiAdapter.BuildSectionsPayload</c>), e.g.
    /// <c>"BusinessPlan"</c> — NOT the kebab-case keys in
    /// <see cref="ReAccreditationQuerySections"/>, which only apply to
    /// <see cref="ResumeFromQueryRequest.SectionKeys"/>, a separate field.
    /// <see cref="ResumeFromQueryRequest.Sections"/> is keyed independently
    /// by whatever the operator backend sends, and it sends its own enum
    /// name, not the Case Management service checkbox key.
    ///
    /// Only covers the sections a resubmit can actually change on this
    /// field set: <c>authority-to-issue</c> is deliberately absent — a
    /// separate, unrelated code path already merges it into its canonical
    /// field, so re-merging it here would be redundant. <c>broadly-equivalent-standards</c>
    /// and <c>overseas-reprocessing-sites</c> are also absent: neither has a
    /// documented stale-read bug, and ORS data lives nested per-site under
    /// <c>payload.overseasSites.sites[]</c> rather than as a flat section
    /// value, so a blind top-level overwrite here would be wrong.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> s_canonicalPayloadFieldBySectionKey =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BusinessPlan"] = "businessPlan",
            ["Prns"] = "prns",
            ["SamplingPlan"] = "samplingPlan",
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

        // RA-523: keep the whole query audit entry, not just its actionId — its
        // CreatedBy/CreatedByName identify the case worker who raised the
        // query, which RestoreQuerierAssignmentAsync needs once the resume has
        // landed.
        var queryEntry = workItem
            .AuditLog
            .Where(entry => string.Equals(entry.Action, ReAccreditationQueryService.AuditAction, StringComparison.Ordinal))
            .OrderByDescending(entry => entry.CreatedAt)
            .FirstOrDefault(entry => entry.Details.GetValueOrDefault("actionId") is not null);

        var queryActionId = queryEntry?.Details.GetValueOrDefault("actionId");

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

        await RestoreQuerierAssignmentAsync(
            workItemId, result.WorkItem, queryEntry!, user, cancellationToken);

        // Re-read so the response carries the query-responded audit entry
        // the out-of-band appender wrote against its own copy of the document,
        // plus any assignment RestoreQuerierAssignmentAsync just re-established.
        var refreshed = await persistence.GetByIdAsync(workItemId, cancellationToken);
        return refreshed is null ? result : WorkItemActionResult.Success(refreshed);
    }

    /// <summary>
    /// RA-523: put the application back in the hands of the case worker who
    /// raised the query, once the operator's resubmission has landed.
    ///
    /// <see cref="ReAccreditationQueryService"/> self-assigns at query time
    /// (RA-291) and the CM query page promises exactly that, but that
    /// assignment is not durable across the query window: CM offers
    /// unassign/reassign unconditionally on any non-terminal item, <c>queried</c>
    /// included, so an application can sit in <c>queried</c> owned by nobody.
    /// Nothing then re-established ownership when the operator resubmitted, so
    /// the work item came back <c>updated</c> and unassigned — the reported
    /// defect. This closes that gap at the only point that can: the resume.
    ///
    /// Deliberately conditional on the item being <b>unassigned</b>. An item a
    /// supervisor handed to a different case worker mid-query is theirs; yanking
    /// it back to the original querier on an operator-driven resubmission would
    /// silently undo a deliberate human decision. (Re-assigning to the querier
    /// when they already hold it would be an engine no-op anyway.)
    ///
    /// Routed through <see cref="IWorkItemService.AssignAsync"/> rather than
    /// written onto the document so the normal <c>assigned</c> audit entry and
    /// the RA-237 OfficerAssignment notification fire exactly as they do for a
    /// manual assign (AC05). It is attributed to the resume caller — the
    /// operator's resubmission is genuinely what caused it — while the
    /// <c>assigneeId</c>/<c>assigneeName</c> detail names the case worker.
    ///
    /// Runs AFTER the transition, and a failure here never fails the resume:
    /// the state change is already persisted and is what the operator backend's
    /// contract depends on, so a lost assignment is logged, not surfaced as a
    /// 4xx/5xx that would make the operator's resubmission look rejected. That
    /// holds for a thrown infrastructure fault (a Mongo blip, a driver timeout)
    /// just as much as for a failure code, hence the catch-all here — without
    /// it, an exception out of <see cref="IWorkItemService.AssignAsync"/> would
    /// surface a completed, already-persisted resubmission to the operator
    /// backend as a 5xx, which is exactly the failure mode the ordering
    /// decision above exists to prevent.
    ///
    /// A cancellation raised by the caller's own token is deliberately NOT
    /// swallowed: that is the caller giving up on the request, not the restore
    /// failing, and the rest of the pipeline needs to see it.
    /// </summary>
    private async Task RestoreQuerierAssignmentAsync(
        Guid workItemId,
        WorkItem? current,
        WorkItemAuditEntry queryEntry,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        try
        {
            await RestoreQuerierAssignmentCoreAsync(
                workItemId, current, queryEntry, user, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Work item {WorkItemId} resumed from query but the assignment could not be " +
                "restored to the querying case worker {UserId}; it remains unassigned.",
                workItemId, queryEntry.CreatedBy);
        }
    }

    /// <summary>
    /// The restore itself. Always called through
    /// <see cref="RestoreQuerierAssignmentAsync"/>, which owns the guarantee
    /// that nothing here can fail the resume.
    /// </summary>
    private async Task RestoreQuerierAssignmentCoreAsync(
        Guid workItemId,
        WorkItem? current,
        WorkItemAuditEntry queryEntry,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (current is null || !string.IsNullOrWhiteSpace(current.AssignedToId))
        {
            return;
        }

        var querierId = queryEntry.CreatedBy;
        if (string.IsNullOrWhiteSpace(querierId))
        {
            // Pre-RA-97 / machine-raised query entries carry no actor. Nothing
            // to restore to; leave the item unassigned for a caseworker to pick up.
            logger.LogWarning(
                "Work item {WorkItemId} resumed from query while unassigned, but the query audit " +
                "entry records no raising user, so no assignment could be restored.",
                workItemId);
            return;
        }

        // The resume transition's post-action hooks (notification, status push,
        // SLA) write to the same document, some of them on the background queue,
        // so this assignment routinely loses the optimistic-concurrency race
        // with a write that landed between AssignAsync's own read and its
        // replace. AssignAsync re-reads on every call, so simply retrying is
        // enough — same bounded-retry shape as ReAccreditationNationRoutingHook.
        // The delay is a real timer, deliberately not the injected TimeProvider:
        // it is a backoff, not a timestamp, and tests substitute time.
        for (var attempt = 1; attempt <= MaxAssignAttempts; attempt++)
        {
            var assignResult = await engine.AssignAsync(
                workItemId, querierId, queryEntry.CreatedByName, user, cancellationToken);

            if (assignResult.IsSuccess || assignResult.IsIdempotentReplay)
            {
                logger.LogInformation(
                    "Work item {WorkItemId} re-assigned to the querying case worker {UserId} " +
                    "after resume from query.",
                    workItemId, querierId);
                return;
            }

            if (assignResult.FailureCode != WorkItemActionFailureCode.ConcurrencyConflict
                || attempt == MaxAssignAttempts)
            {
                logger.LogError(
                    "Work item {WorkItemId} resumed from query but could not be re-assigned to the " +
                    "querying case worker {UserId} ({FailureCode}) after {Attempts} attempt(s); " +
                    "it remains unassigned.",
                    workItemId, querierId, assignResult.FailureCode, attempt);
                return;
            }

            await Task.Delay(s_assignRetryDelay * attempt, cancellationToken);
        }
    }

    /// <summary>
    /// Write the resubmitted section values and file references onto
    /// <c>payload.latestSections</c> BEFORE the transition — same targeted
    /// single-field-write convention as
    /// <see cref="ReAccreditationQueryService"/>'s <c>currentQuery</c> stamp,
    /// for the same reason (a full-payload replace would materialise
    /// modelled-but-absent fields as explicit nulls).
    ///
    /// RA-291 regression fix: also merges each resubmitted section that has a
    /// canonical top-level field (<see cref="s_canonicalPayloadFieldBySectionKey"/>)
    /// back onto that field, e.g. <c>sections["BusinessPlan"]</c> onto
    /// <c>payload.businessPlan</c>. Without this, only <c>latestSections</c>
    /// was ever updated, which nothing — including the case management
    /// summary page — reads back, so a resubmitted business plan / PRN
    /// tonnage / sampling plan never displayed after a query.
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
                var sectionValue = WorkItemPayloadConverter.ToBson(value);
                sectionsDoc[sectionKey] = sectionValue;

                if (s_canonicalPayloadFieldBySectionKey.TryGetValue(sectionKey, out var canonicalField))
                {
                    var canonicalMatched = await persistence.SetPayloadFieldAsync(
                        workItemId, canonicalField, sectionValue.DeepClone(), cancellationToken);
                    if (!canonicalMatched)
                    {
                        return WorkItemActionResult.Failure(
                            WorkItemActionFailureCode.WorkItemNotFound,
                            $"No work item exists with id '{workItemId}'.");
                    }
                }
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
