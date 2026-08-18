using System.Security.Claims;
using System.Text.Json;
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

    // RA-413: the canonical payload fields the regulator "Application details"
    // page (management-fe application-summary.js) reads. The resubmitted
    // section values MUST land here — writing them only to `latestSections`
    // (which nothing reads) left the regulator seeing stale pre-query values.
    public const string PrnsPayloadField = "prns";
    public const string BusinessPlanPayloadField = "businessPlan";
    public const string SamplingPlanPayloadField = "samplingPlan";
    private const string SamplingPlanFilesField = "files";

    // The subset of the closed six-key section set (ReAccreditationQuerySections)
    // this build merges into canonical payload fields. prn-tonnage and
    // authority-to-issue both nest under `payload.prns`; business-plan is the
    // whole `payload.businessPlan` document; sampling-and-inspection-plan
    // drives `payload.samplingPlan.files` from the request's file references.
    private const string PrnTonnageSection = "prn-tonnage";
    private const string AuthorityToIssueSection = "authority-to-issue";
    private const string BusinessPlanSection = "business-plan";
    private const string SamplingPlanSection = "sampling-and-inspection-plan";

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

        // RA-413: also merge the resubmitted values into the canonical payload
        // fields the regulator UI actually reads, so a resumed application shows
        // the operator's changes rather than the stale pre-query snapshot.
        var mergeFailure = await MergeResubmittedCanonicalSectionsAsync(workItem, request, cancellationToken);
        if (mergeFailure is not null)
        {
            return mergeFailure;
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

        return await SetPayloadFieldOrFailAsync(
            workItemId, LatestSectionsPayloadField, latestSections, cancellationToken);
    }

    /// <summary>
    /// RA-413: merge the resubmitted section values and file references into the
    /// canonical payload fields the regulator "Application details" page reads
    /// (management-fe <c>application-summary.js</c>, live-fetched every render):
    /// <list type="bullet">
    /// <item><c>prn-tonnage</c> / <c>authority-to-issue</c> → fields under
    /// <c>payload.prns</c> (e.g. <c>plannedTonnageBand</c>, <c>authorisers</c>).</item>
    /// <item><c>business-plan</c> → the whole <c>payload.businessPlan</c> document.</item>
    /// <item><c>sampling-and-inspection-plan</c> → <c>payload.samplingPlan.files</c>,
    /// rebuilt from the request's file references for that section.</item>
    /// </list>
    ///
    /// <para>
    /// Only sections named in <see cref="ResumeFromQueryRequest.SectionKeys"/>
    /// (and, for value merges, actually present in
    /// <see cref="ResumeFromQueryRequest.Sections"/>) are touched, so a section
    /// the operator did not resubmit keeps its pre-query value. Merges are done
    /// against a clone of the currently-persisted payload document so untouched
    /// siblings (e.g. <c>prns.authorisers</c> when only <c>prn-tonnage</c> was
    /// resubmitted) are preserved. Each canonical field is written as a single
    /// top-level payload field via <see cref="IWorkItemPersistence.SetPayloadFieldAsync"/>,
    /// the same targeted-write convention as the <c>latestSections</c> stamp.
    /// </para>
    /// </summary>
    private async Task<WorkItemActionResult?> MergeResubmittedCanonicalSectionsAsync(
        WorkItem workItem,
        ResumeFromQueryRequest request,
        CancellationToken cancellationToken)
    {
        var resubmitted = new HashSet<string>(request.SectionKeys ?? [], StringComparer.Ordinal);
        var sections = request.Sections;

        bool HasSectionData(string sectionKey) =>
            resubmitted.Contains(sectionKey) && sections is not null && sections.ContainsKey(sectionKey);

        // prn-tonnage + authority-to-issue both nest under payload.prns, so
        // merge whichever were resubmitted into one clone and write it once.
        if (HasSectionData(PrnTonnageSection) || HasSectionData(AuthorityToIssueSection))
        {
            var prns = ClonePayloadDocument(workItem, PrnsPayloadField);
            MergeSectionFields(prns, sections, PrnTonnageSection);
            MergeSectionFields(prns, sections, AuthorityToIssueSection);

            var failure = await SetPayloadFieldOrFailAsync(
                workItem.Id, PrnsPayloadField, prns, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }
        }

        // business-plan is a single cohesive section — its value object IS the
        // canonical payload.businessPlan document.
        if (HasSectionData(BusinessPlanSection))
        {
            var businessPlan = WorkItemPayloadConverter.ToBson(sections![BusinessPlanSection]);
            var failure = await SetPayloadFieldOrFailAsync(
                workItem.Id, BusinessPlanPayloadField, businessPlan, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }
        }

        // sampling-and-inspection-plan: the resubmitted file references are the
        // operator backend's complete current file list for the section, so
        // replace payload.samplingPlan.files with them (preserving any other
        // samplingPlan sub-fields). Gated on section membership alone: an empty
        // list is a legitimate "the operator removed the files" outcome.
        if (resubmitted.Contains(SamplingPlanSection))
        {
            var samplingPlan = ClonePayloadDocument(workItem, SamplingPlanPayloadField);
            samplingPlan[SamplingPlanFilesField] = BuildSamplingPlanFiles(request.FileReferences);

            var failure = await SetPayloadFieldOrFailAsync(
                workItem.Id, SamplingPlanPayloadField, samplingPlan, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    /// <summary>
    /// Copy every field of the resubmitted <paramref name="sectionKey"/> value
    /// object onto <paramref name="target"/>, overwriting only those keys. No-op
    /// when the section was not resubmitted with a value.
    /// </summary>
    private static void MergeSectionFields(
        BsonDocument target,
        IReadOnlyDictionary<string, JsonElement>? sections,
        string sectionKey)
    {
        if (sections is null || !sections.TryGetValue(sectionKey, out var value))
        {
            return;
        }

        // Validator guarantees each section value is a JSON object, so ToBson
        // yields a BsonDocument here.
        foreach (var element in WorkItemPayloadConverter.ToBson(value))
        {
            target[element.Name] = element.Value;
        }
    }

    /// <summary>
    /// Build the <c>payload.samplingPlan.files</c> array from the request's
    /// file references for the sampling section. Element shape matches what
    /// management-fe's <c>download-file.controller</c> and
    /// <c>application-summary.js</c> read: <c>fileId</c> (used to resolve the
    /// download), <c>filename</c> (displayed), and <c>s3Key</c> (required for
    /// the S3 fetch). <c>contentType</c>/<c>s3Bucket</c>/<c>scanStatus</c> are
    /// not carried by <see cref="SectionFileReference"/>; the FE falls back to
    /// a default bucket + content type, and renders the file with a "Pending"
    /// scan tag (no download link) until a scan result populates the status.
    /// </summary>
    private static BsonArray BuildSamplingPlanFiles(IReadOnlyList<SectionFileReference>? fileReferences) =>
        new((fileReferences ?? [])
            .Where(f => string.Equals(f.SectionKey, SamplingPlanSection, StringComparison.Ordinal))
            .Select(f => new BsonDocument
            {
                ["fileId"] = ToBsonValue(f.FileId),
                ["filename"] = ToBsonValue(f.Filename),
                ["s3Key"] = ToBsonValue(f.S3Key),
            }));

    /// <summary>
    /// Deep-clone the named document field out of the currently-loaded payload
    /// so merges preserve untouched siblings, or start a fresh document when
    /// the field is absent or not a document.
    /// </summary>
    private static BsonDocument ClonePayloadDocument(WorkItem workItem, string fieldName) =>
        workItem.Payload.TryGetValue(fieldName, out var existing) && existing is BsonDocument document
            ? (BsonDocument)document.DeepClone()
            : new BsonDocument();

    private async Task<WorkItemActionResult?> SetPayloadFieldOrFailAsync(
        Guid workItemId,
        string fieldName,
        BsonValue value,
        CancellationToken cancellationToken)
    {
        var matched = await persistence.SetPayloadFieldAsync(workItemId, fieldName, value, cancellationToken);
        return matched
            ? null
            : WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'.");
    }

    private static BsonValue ToBsonValue(string? value) => value is null ? BsonNull.Value : new BsonString(value);

    private static string SerialiseFileReferences(IReadOnlyList<SectionFileReference>? fileReferences) =>
        fileReferences is null || fileReferences.Count == 0
            ? string.Empty
            : string.Join(";", fileReferences.Select(f => $"{f.SectionKey}:{f.FileId}:{f.Filename}"));
}
