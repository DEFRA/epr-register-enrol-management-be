using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;

/// <summary>
/// RA-698: <c>POST /work-items/add-overseas-site/{id}/resume-from-query</c>
/// body, as the operator backend's <c>ResumeChangeRequestItemFromQueryAsync</c>
/// sends it (camelCase). <see cref="Site"/> holds the changed ORS values and
/// each <see cref="AddOverseasSiteInterimSiteChange"/> the changed values of
/// one interim site, by <see cref="AddOverseasSiteResumeRules.SiteFields"/> /
/// <see cref="AddOverseasSiteResumeRules.InterimSiteFields"/> only. Every
/// member is nullable so a structurally broken body reaches
/// <see cref="AddOverseasSiteResumeValidator"/> and is a ProblemDetails 400.
/// </summary>
internal sealed record AddOverseasSiteResumeRequest(
    AddOverseasSiteResponder? ResponderContactDetails,
    DateTime? SubmittedAt,
    Dictionary<string, string?>? Site,
    IReadOnlyList<AddOverseasSiteInterimSiteChange>? InterimSites,
    IReadOnlyList<AddOverseasSiteBesFile>? BesEvidenceFiles
);

internal sealed record AddOverseasSiteResponder(string? FullName, string? Email, string? Role);

/// <summary>One interim site's changed values, matched on <c>siteNumber</c> (or, failing that, <c>siteId</c>).</summary>
internal sealed record AddOverseasSiteInterimSiteChange(
    int? SiteId,
    string? SiteNumber,
    Dictionary<string, string?>? Changes
);

internal sealed record AddOverseasSiteBesFile(
    string? FileId,
    string? Filename,
    string? ContentType,
    string? S3Key,
    string? S3Bucket,
    string? BesEvidenceValidFromDate,
    string? BesEvidenceExpiryDate
);

/// <summary>The fields a query response may change: everything else on the ORS stays as submitted.</summary>
internal static class AddOverseasSiteResumeRules
{
    private static readonly string[] s_interimSiteFields =
    [
        "siteName",
        "addressLine1",
        "addressLine2",
        "townOrCity",
        "stateOrRegion",
        "postcode",
        "contactName",
        "contactEmail",
        "contactPhone",
    ];

    /// <summary>The ORS fields (country, coordinates, operations and the rest are locked).</summary>
    public static readonly IReadOnlySet<string> SiteFields = new HashSet<string>(
        [.. s_interimSiteFields, "rejectedLoadsArrangements"],
        StringComparer.Ordinal
    );

    /// <summary>The interim site fields: name, address and contact details, no arrangements.</summary>
    public static readonly IReadOnlySet<string> InterimSiteFields = new HashSet<string>(
        s_interimSiteFields,
        StringComparer.Ordinal
    );

    /// <summary>The longest value accepted for any field.</summary>
    public const int MaxValueLength = 4000;
}

/// <summary>
/// RA-698: request validation for add-ORS resume-from-query. Returns the
/// human-readable failure detail, or <c>null</c> when valid. Unknown or locked
/// field keys are refused rather than ignored, so a hand-crafted call cannot
/// overwrite the ORS's country, coordinates or recycling operations; values are
/// length-capped. The response must change something: a changed ORS or interim
/// site value, or a new BES file.
/// </summary>
internal static class AddOverseasSiteResumeValidator
{
    public const string MissingResponderMessage =
        "responderContactDetails.fullName, .email and .role are all required";
    public const string NothingChangedMessage =
        "the response must change a site or interim site field, or add a BES evidence file";
    public const string UnknownSiteFieldMessage = "site contains a field that cannot be changed in a query response";
    public const string UnknownInterimSiteFieldMessage =
        "interimSites[].changes contains a field that cannot be changed in a query response";
    public const string InterimSiteNotIdentifiedMessage = "each interim site change needs a siteNumber or siteId";
    public const string FieldTooLongMessage = "a changed value is too long";
    public const string BesFileIncompleteMessage = "each besEvidenceFiles entry needs a fileId, filename and s3Key";

    public static string? Validate(AddOverseasSiteResumeRequest? request)
    {
        if (request?.ResponderContactDetails is not { } responder
            || string.IsNullOrWhiteSpace(responder.FullName)
            || string.IsNullOrWhiteSpace(responder.Email)
            || string.IsNullOrWhiteSpace(responder.Role)
        )
        {
            return MissingResponderMessage;
        }

        var site = request.Site ?? [];
        if (site.Keys.Any(k => !AddOverseasSiteResumeRules.SiteFields.Contains(k)))
        {
            // Deliberately does not echo the offending key: unvalidated client input.
            return UnknownSiteFieldMessage;
        }

        var interimSites = request.InterimSites ?? [];
        foreach (var interim in interimSites)
        {
            if (interim is null || (interim.SiteId is null && string.IsNullOrWhiteSpace(interim.SiteNumber)))
            {
                return InterimSiteNotIdentifiedMessage;
            }

            if ((interim.Changes ?? []).Keys.Any(k => !AddOverseasSiteResumeRules.InterimSiteFields.Contains(k)))
            {
                return UnknownInterimSiteFieldMessage;
            }
        }

        var values = site.Values.Concat(interimSites.SelectMany(i => (i.Changes ?? []).Values));
        if (values.Any(v => v is { Length: > AddOverseasSiteResumeRules.MaxValueLength }))
        {
            return FieldTooLongMessage;
        }

        var besFiles = request.BesEvidenceFiles ?? [];
        if (
            besFiles.Any(f =>
                f is null
                || string.IsNullOrWhiteSpace(f.FileId)
                || string.IsNullOrWhiteSpace(f.Filename)
                || string.IsNullOrWhiteSpace(f.S3Key)
            )
        )
        {
            return BesFileIncompleteMessage;
        }

        var changesAnything =
            site.Count > 0 || interimSites.Any(i => (i.Changes ?? []).Count > 0) || besFiles.Count > 0;
        return changesAnything ? null : NothingChangedMessage;
    }
}

/// <summary>
/// RA-698: records the operator's answer to an add-ORS query.
/// </summary>
internal interface IAddOverseasSiteResumeService
{
    /// <summary>
    /// Validate the item is <c>queried</c>, apply the changed values to
    /// <c>payload.site</c> and its interim sites, stamp the response on the
    /// payload, move the item to <c>updated</c> (or, for an item queried after
    /// it was duly made, straight to assessment, as for any Standard-workflow
    /// resume) and record who responded, what changed (old and new) and the new
    /// BES files on the audit log. The <c>resume-during-*</c> transition is
    /// resolved from the item's own query audit entry. An item that has already
    /// left <c>queried</c> for a resume target succeeds: an identical response is
    /// an idempotent no-op, a differing one (a changed value or new BES file, as
    /// the operator backend resubmits after its own concurrent-edit 409) is
    /// applied to the site and audited without a transition; any other state is an
    /// <see cref="WorkItemActionFailureCode.InvalidTransition"/>.
    /// </summary>
    Task<WorkItemActionResult> ResumeFromQueryAsync(
        Guid workItemId,
        AddOverseasSiteResumeRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// RA-698 default <see cref="IAddOverseasSiteResumeService"/>, following
/// <c>ReAccreditationResumeService</c>: the response is written to the payload
/// before the transition (so a failed transition is retried with the values
/// already saved, never lost to an idempotent replay), then the transition,
/// then the audit entry. The status push for the move onto Updated is
/// suppressed for this type
/// (<see cref="AddOverseasSiteLifecycleStrategy.PushesQueryResponseStatus"/>):
/// the operator journey sent this response and already holds that status.
/// </summary>
internal sealed class AddOverseasSiteResumeService(
    IWorkItemPersistence persistence,
    IWorkItemService engine,
    IWorkItemAuditAppender auditAppender,
    ILogger<AddOverseasSiteResumeService> logger,
    TimeProvider? timeProvider = null
) : IAddOverseasSiteResumeService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>The same audit action id as re-accreditation's, so the application history renders it alike.</summary>
    public const string AuditAction = "query-responded";

    public const string AuditActionDisplayName = "Query responded";

    /// <summary>The display name of the entry for a differing replay applied after the item had already moved on.</summary>
    public const string ReplayAuditActionDisplayName = "Query response updated";

    /// <summary>The payload field the latest response is stamped under.</summary>
    public const string LatestResponsePayloadField = "latestQueryResponse";

    private const string InterimSitesField = "interimSites";

    private const string RejectedLoadsWireField = "rejectedLoadsArrangements";

    private const string RepatriatedLoadsPayloadField = "repatriatedLoads";

    private const string BesEvidenceField = "besEvidence";

    private const int MaxAssignAttempts = 4;

    private static readonly TimeSpan s_assignRetryDelay = TimeSpan.FromMilliseconds(25);

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public async Task<WorkItemActionResult> ResumeFromQueryAsync(
        Guid workItemId,
        AddOverseasSiteResumeRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
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

        if (!string.Equals(workItem.StateId, StandardQueryActions.QueriedStateId, StringComparison.OrdinalIgnoreCase))
        {
            // A duplicate resubmit (a double-click) must not fail the retry; any
            // other state means this item was never waiting on this call.
            if (StandardQueryActions.ResumeTargetStates.Contains(workItem.StateId))
            {
                return await ReplayAsync(workItem, request, user, cancellationToken);
            }

            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item {workItemId} is in state '{workItem.StateId}' and cannot be resumed from a query."
            );
        }

        // The whole query audit entry, not just its action id: its CreatedBy
        // identifies the caseworker who queried, whom the item is re-assigned
        // to once the resume has landed.
        var queryEntry = FindQueryEntry(workItem);

        var resumeActionId = StandardQueryActions.ResolveResumeActionId(queryEntry?.Details.GetValueOrDefault("actionId"));
        if (queryEntry is null || resumeActionId is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item {workItemId} is 'queried' but its query action could not be resolved "
                    + "from its audit history, so the matching resume action cannot be determined."
            );
        }

        var site = SubmissionPayload.ReadSite(workItem.Payload)?.DeepClone().AsBsonDocument;
        if (site is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item {workItemId} has no payload.site to apply the response to."
            );
        }

        var siteChanges = ApplyChanges(site, request.Site);
        var interimChanges = ApplyInterimSiteChanges(site, request.InterimSites, out var unmatchedInterimSites);
        // A retry after a failed transition finds the payload already updated, so
        // the diff above is empty: carry the original "from" values over from the
        // response stamped earlier in this same query round.
        var priorResponse = PriorResponseThisRound(workItem, queryEntry);
        if (priorResponse is not null)
        {
            siteChanges = MergeChanges(priorResponse.GetValue("siteChanges", null) as BsonDocument, siteChanges);
            interimChanges = MergeInterimChanges(priorResponse.GetValue("interimSiteChanges", null) as BsonArray, interimChanges);
        }

        AppendBesFiles(site, request.BesEvidenceFiles);
        LogUnmatchedInterimSites(workItemId, unmatchedInterimSites);

        var stampFailure = await StampAsync(workItemId, site, request, user, siteChanges, interimChanges, cancellationToken);
        if (stampFailure is not null)
        {
            return stampFailure;
        }

        var result = await engine.ApplyActionAsync(workItemId, resumeActionId, user, cancellationToken);
        if (!result.IsSuccess)
        {
            return result;
        }

        var besFiles = request.BesEvidenceFiles ?? [];
        await AppendAuditAsync(
            workItemId,
            AuditActionDisplayName,
            BuildAuditDetails(resumeActionId, request, siteChanges, interimChanges, unmatchedInterimSites),
            resumeActionId,
            user
        );

        logger.LogInformation(
            "Add-ORS work item {WorkItemId} resumed from query by {UserId} via {ActionId}: {SiteFieldCount} site field(s), {InterimCount} interim site(s), {BesCount} BES file(s)",
            workItemId,
            user.FindFirstValue("user:id"),
            resumeActionId,
            siteChanges.Count,
            interimChanges.Count,
            besFiles.Count
        );

        await RestoreQuerierAssignmentAsync(workItemId, result.WorkItem, queryEntry, user, cancellationToken);

        // Re-read so the response carries the audit entry the out-of-band
        // appender wrote and any re-established assignment.
        var refreshed = await persistence.GetByIdAsync(workItemId, cancellationToken);
        return refreshed is null ? result : WorkItemActionResult.Success(refreshed);
    }

    /// <summary>The newest query audit entry that names its action (its CreatedBy is the caseworker who queried).</summary>
    private static WorkItemAuditEntry? FindQueryEntry(WorkItem workItem) =>
        workItem
            .AuditLog.Where(e => string.Equals(e.Action, AddOverseasSiteQueryService.AuditAction, StringComparison.Ordinal))
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefault(e => e.Details.GetValueOrDefault("actionId") is not null);

    private void LogUnmatchedInterimSites(Guid workItemId, List<string> unmatched)
    {
        if (unmatched.Count > 0)
        {
            logger.LogWarning(
                "Resume-from-query for work item {WorkItemId}: {Count} interim site change(s) matched no interim site on the payload and were recorded on the audit log only.",
                workItemId,
                unmatched.Count
            );
        }
    }

    /// <summary>
    /// A resume for an item that already left <c>queried</c>. The operator
    /// backend retries a resume it did not see succeed, and a second tab can
    /// add an answer or BES file after the first resume (the backend then
    /// refuses to mark the item Updated and the operator resubmits the fuller
    /// response). A response that adds nothing to what the payload already
    /// holds stays an idempotent no-op; one that differs (a changed value or a
    /// new BES file) is applied to the site, re-stamped as the latest response
    /// and audited, with no transition (the item has already moved).
    /// <c>submittedAt</c> and the responder are not compared: a retry sends a
    /// fresh timestamp.
    /// </summary>
    private async Task<WorkItemActionResult> ReplayAsync(
        WorkItem workItem,
        AddOverseasSiteResumeRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var workItemId = workItem.Id;
        var site = SubmissionPayload.ReadSite(workItem.Payload)?.DeepClone().AsBsonDocument;
        if (site is null)
        {
            return NoOpReplay(workItem);
        }

        var siteChanges = ApplyChanges(site, request.Site);
        var interimChanges = ApplyInterimSiteChanges(site, request.InterimSites, out var unmatchedInterimSites);
        var newFileCount = AppendBesFiles(site, request.BesEvidenceFiles);
        if (siteChanges.Count == 0 && interimChanges.All(c => c.Changes.Count == 0) && newFileCount == 0)
        {
            return NoOpReplay(workItem);
        }

        // Keep the original "from" values of this query round (the earlier response already applied some).
        var queryEntry = FindQueryEntry(workItem);
        var priorResponse = queryEntry is null ? null : PriorResponseThisRound(workItem, queryEntry);
        if (priorResponse is not null)
        {
            siteChanges = MergeChanges(priorResponse.GetValue("siteChanges", null) as BsonDocument, siteChanges);
            interimChanges = MergeInterimChanges(priorResponse.GetValue("interimSiteChanges", null) as BsonArray, interimChanges);
        }

        LogUnmatchedInterimSites(workItemId, unmatchedInterimSites);
        var stampFailure = await StampAsync(workItemId, site, request, user, siteChanges, interimChanges, cancellationToken);
        if (stampFailure is not null)
        {
            return stampFailure;
        }

        await AppendAuditAsync(
            workItemId,
            ReplayAuditActionDisplayName,
            BuildAuditDetails(null, request, siteChanges, interimChanges, unmatchedInterimSites),
            workItem.StateId,
            user
        );
        logger.LogInformation(
            "Add-ORS work item {WorkItemId} in state '{StateId}': a differing resume replay was applied ({SiteFieldCount} site field(s), {InterimCount} interim site(s), {BesCount} new BES file(s)).",
            workItemId,
            workItem.StateId,
            siteChanges.Count,
            interimChanges.Count,
            newFileCount
        );

        var refreshed = await persistence.GetByIdAsync(workItemId, cancellationToken);
        return WorkItemActionResult.Success(refreshed ?? workItem);
    }

    private WorkItemActionResult NoOpReplay(WorkItem workItem)
    {
        logger.LogInformation(
            "Resume-from-query for work item {WorkItemId} is a no-op: already in state '{StateId}'.",
            workItem.Id,
            workItem.StateId
        );
        return WorkItemActionResult.IdempotentReplay(workItem);
    }

    private Dictionary<string, string?> BuildAuditDetails(
        string? resumeActionId,
        AddOverseasSiteResumeRequest request,
        Dictionary<string, FieldChange> siteChanges,
        List<InterimSiteChange> interimChanges,
        List<string> unmatchedInterimSites
    ) =>
        new()
        {
            ["actionId"] = resumeActionId,
            ["responderFullName"] = request.ResponderContactDetails?.FullName,
            ["responderEmail"] = request.ResponderContactDetails?.Email,
            ["responderRole"] = request.ResponderContactDetails?.Role,
            ["submittedAt"] = (request.SubmittedAt ?? _timeProvider.GetUtcNow().UtcDateTime).ToString("O", CultureInfo.InvariantCulture),
            ["siteChanges"] = JsonSerializer.Serialize(ToWire(siteChanges), s_json),
            ["interimSiteChanges"] = JsonSerializer.Serialize(
                interimChanges.Select(c => new { c.SiteId, c.SiteNumber, changes = ToWire(c.Changes) }),
                s_json
            ),
            ["unmatchedInterimSites"] = string.Join(",", unmatchedInterimSites),
            ["fileReferences"] = string.Join(
                ";",
                (request.BesEvidenceFiles ?? []).Select(f => $"bes-evidence:{f.FileId}:{f.Filename}")
            ),
        };

    private async Task AppendAuditAsync(
        Guid workItemId,
        string displayName,
        Dictionary<string, string?> details,
        string contextId,
        ClaimsPrincipal user
    )
    {
        var appended = await auditAppender.AppendAsync(
            workItemId,
            action: AuditAction,
            actionDisplayName: displayName,
            details: details,
            user,
            // The write is committed: the audit record of what changed must be written regardless.
            CancellationToken.None
        );
        if (!appended)
        {
            logger.LogError(
                "Query-responded audit entry could not be appended to work item {WorkItemId} ({Context}).",
                workItemId,
                contextId
            );
        }
    }

    /// <summary>A changed field's value before and after the response.</summary>
    private sealed record FieldChange(string? From, string? To);

    private sealed record InterimSiteChange(int? SiteId, string? SiteNumber, Dictionary<string, FieldChange> Changes);

    private static Dictionary<string, object?> ToWire(Dictionary<string, FieldChange> changes) =>
        changes.ToDictionary(kv => kv.Key, kv => (object?)new { from = kv.Value.From, to = kv.Value.To });

    /// <summary>Sets each changed value on <paramref name="target"/>, returning only the ones that differ.</summary>
    private static Dictionary<string, FieldChange> ApplyChanges(BsonDocument target, Dictionary<string, string?>? changes)
    {
        var applied = new Dictionary<string, FieldChange>(StringComparer.Ordinal);
        foreach (var (field, value) in changes ?? [])
        {
            // The wire key (and the audit/latest-response key) stays as the
            // operator backend sends it; the payload uses the submitted key.
            var payloadKey = PayloadKey(field);
            var previous = ReadString(target, payloadKey) ?? (payloadKey == field ? null : ReadString(target, field));
            if (string.Equals(previous, value, StringComparison.Ordinal))
            {
                continue;
            }

            target[payloadKey] = value is null ? BsonNull.Value : new BsonString(value);
            if (payloadKey != field)
            {
                target.Remove(field);
            }

            applied[field] = new FieldChange(previous, value);
        }

        return applied;
    }

    private static string? ReadString(BsonDocument document, string key) =>
        document.TryGetValue(key, out var existing) && existing.IsString ? existing.AsString : null;

    /// <summary>
    /// The operator backend submits the site's rejected-loads text as
    /// <c>repatriatedLoads</c> but answers a query with
    /// <c>rejectedLoadsArrangements</c>; management-fe reads the former.
    /// </summary>
    private static string PayloadKey(string wireField) =>
        string.Equals(wireField, RejectedLoadsWireField, StringComparison.Ordinal) ? RepatriatedLoadsPayloadField : wireField;

    /// <summary>The response stamped since the item was last queried, if any (the item is still queried, so only a failed earlier attempt can have left one).</summary>
    private static BsonDocument? PriorResponseThisRound(WorkItem workItem, WorkItemAuditEntry queryEntry)
    {
        if (
            !workItem.Payload.TryGetValue(LatestResponsePayloadField, out var latest)
            || latest is not BsonDocument document
            || !document.TryGetValue("respondedAt", out var respondedAt)
            || !respondedAt.IsBsonDateTime
        )
        {
            return null;
        }

        return respondedAt.ToUniversalTime() >= queryEntry.CreatedAt.ToUniversalTime() ? document : null;
    }

    private static Dictionary<string, FieldChange> MergeChanges(BsonDocument? prior, Dictionary<string, FieldChange> fresh)
    {
        var merged = new Dictionary<string, FieldChange>(fresh, StringComparer.Ordinal);
        foreach (var element in prior ?? new BsonDocument())
        {
            if (element.Value is not BsonDocument change)
            {
                continue;
            }

            var from = change.GetValue("from", BsonNull.Value);
            var original = from.IsString ? from.AsString : null;
            string? to;
            if (merged.TryGetValue(element.Name, out var latest))
            {
                to = latest.To;
            }
            else
            {
                to = change.GetValue("to", BsonNull.Value) is { IsString: true } t ? t.AsString : null;
            }

            merged[element.Name] = new FieldChange(original, to);
        }

        return merged.Where(kv => !string.Equals(kv.Value.From, kv.Value.To, StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    private static List<InterimSiteChange> MergeInterimChanges(BsonArray? prior, List<InterimSiteChange> fresh)
    {
        var merged = fresh.ToList();
        foreach (var entry in (prior ?? new BsonArray()).OfType<BsonDocument>())
        {
            var siteNumber = entry.GetValue("siteNumber", BsonNull.Value) is { IsString: true } n ? n.AsString : null;
            var siteId = entry.GetValue("siteId", BsonNull.Value) is { IsInt32: true } i ? i.AsInt32 : (int?)null;
            var existing = merged.FindIndex(c => c.SiteNumber == siteNumber && c.SiteId == siteId);
            var changes = MergeChanges(
                entry.GetValue("changes", null) as BsonDocument,
                existing >= 0 ? merged[existing].Changes : []
            );
            var combined = new InterimSiteChange(siteId, siteNumber, changes);
            if (existing >= 0)
            {
                merged[existing] = combined;
            }
            else
            {
                merged.Add(combined);
            }
        }

        return merged;
    }

    /// <summary>Appends the response's new BES files to <c>site.besEvidence.files</c>, skipping any <c>fileId</c> already there; returns how many were added.</summary>
    private static int AppendBesFiles(BsonDocument site, IReadOnlyList<AddOverseasSiteBesFile>? files)
    {
        if (files is null || files.Count == 0)
        {
            return 0;
        }

        if (!site.TryGetValue(BesEvidenceField, out var evidence) || evidence is not BsonDocument evidenceDocument)
        {
            evidenceDocument = [];
            site[BesEvidenceField] = evidenceDocument;
        }

        if (!evidenceDocument.TryGetValue("files", out var list) || list is not BsonArray existing)
        {
            existing = [];
            evidenceDocument["files"] = existing;
        }

        var known = existing
            .OfType<BsonDocument>()
            .Select(f => f.GetValue("fileId", BsonNull.Value))
            .Where(v => v.IsString)
            .Select(v => v.AsString)
            .ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var file in files.Where(f => known.Add(f.FileId!)))
        {
            added++;
            existing.Add(
                new BsonDocument
                {
                    ["fileId"] = file.FileId,
                    ["filename"] = file.Filename,
                    ["contentType"] = Text(file.ContentType),
                    ["s3Key"] = file.S3Key,
                    ["s3Bucket"] = Text(file.S3Bucket),
                    ["besEvidenceValidFromDate"] = Text(file.BesEvidenceValidFromDate),
                    ["besEvidenceExpiryDate"] = Text(file.BesEvidenceExpiryDate),
                    // The operator backend only forwards files that passed its scan, so the site copy is Clean;
                    // management-fe findFile reads this record first and serves only Clean files.
                    ["scanStatus"] = "Clean",
                }
            );
        }

        return added;
    }

    private static List<InterimSiteChange> ApplyInterimSiteChanges(
        BsonDocument site,
        IReadOnlyList<AddOverseasSiteInterimSiteChange>? requested,
        out List<string> unmatched
    )
    {
        unmatched = [];
        var applied = new List<InterimSiteChange>();
        var interimSites = site.TryGetValue(InterimSitesField, out var list) && list.IsBsonArray ? list.AsBsonArray : null;

        foreach (var change in requested ?? [])
        {
            var target = interimSites
                ?.OfType<BsonDocument>()
                .FirstOrDefault(i => Matches(i, change));
            if (target is null)
            {
                unmatched.Add(change.SiteNumber ?? change.SiteId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                continue;
            }

            applied.Add(new InterimSiteChange(change.SiteId, change.SiteNumber, ApplyChanges(target, change.Changes)));
        }

        return applied;
    }

    // Matched on the interim site number, which the operator journey and
    // caseworkers see; the numeric id is the fallback for a payload without one.
    private static bool Matches(BsonDocument interim, AddOverseasSiteInterimSiteChange change)
    {
        if (
            !string.IsNullOrWhiteSpace(change.SiteNumber)
            && interim.TryGetValue("siteNumber", out var number)
            && number.IsString
        )
        {
            return string.Equals(number.AsString, change.SiteNumber, StringComparison.OrdinalIgnoreCase);
        }

        return change.SiteId is { } id
            && interim.TryGetValue("siteId", out var siteId)
            && siteId.IsNumeric
            && siteId.ToInt64() == id;
    }

    private async Task<WorkItemActionResult?> StampAsync(
        Guid workItemId,
        BsonDocument updatedSite,
        AddOverseasSiteResumeRequest request,
        ClaimsPrincipal user,
        Dictionary<string, FieldChange> siteChanges,
        List<InterimSiteChange> interimChanges,
        CancellationToken cancellationToken
    )
    {
        // Single-field writes, never a whole-payload replace (which would
        // round-trip the document and could add explicit nulls to indexed fields).
        var siteMatched = await persistence.SetPayloadFieldAsync(
            workItemId,
            SubmissionPayload.SiteField,
            updatedSite,
            cancellationToken
        );

        var latest = new BsonDocument
        {
            ["respondedAt"] = _timeProvider.GetUtcNow().UtcDateTime,
            ["respondedBy"] = Text(user.FindFirstValue("user:id")),
            ["submittedAt"] = (request.SubmittedAt ?? _timeProvider.GetUtcNow().UtcDateTime),
            ["responderContactDetails"] = new BsonDocument
            {
                ["fullName"] = request.ResponderContactDetails?.FullName,
                ["email"] = request.ResponderContactDetails?.Email,
                ["role"] = request.ResponderContactDetails?.Role,
            },
            ["siteChanges"] = ChangesToBson(siteChanges),
            ["interimSiteChanges"] = new BsonArray(
                interimChanges.Select(c => new BsonDocument
                {
                    ["siteId"] = c.SiteId is { } siteId ? new BsonInt32(siteId) : BsonNull.Value,
                    ["siteNumber"] = Text(c.SiteNumber),
                    ["changes"] = ChangesToBson(c.Changes),
                })
            ),
            ["besEvidenceFiles"] = new BsonArray(
                (request.BesEvidenceFiles ?? []).Select(f => new BsonDocument
                {
                    ["fileId"] = f.FileId,
                    ["filename"] = f.Filename,
                    ["contentType"] = Text(f.ContentType),
                    ["s3Key"] = f.S3Key,
                    ["s3Bucket"] = Text(f.S3Bucket),
                    ["besEvidenceValidFromDate"] = Text(f.BesEvidenceValidFromDate),
                    ["besEvidenceExpiryDate"] = Text(f.BesEvidenceExpiryDate),
                })
            ),
        };

        var latestMatched =
            siteMatched
            && await persistence.SetPayloadFieldAsync(workItemId, LatestResponsePayloadField, latest, cancellationToken);

        return latestMatched
            ? null
            : WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."
            );
    }

    private static BsonValue Text(string? value) => value is null ? BsonNull.Value : new BsonString(value);

    private static BsonDocument ChangesToBson(Dictionary<string, FieldChange> changes)
    {
        var document = new BsonDocument();
        foreach (var (field, change) in changes)
        {
            document[field] = new BsonDocument
            {
                ["from"] = Text(change.From),
                ["to"] = Text(change.To),
            };
        }

        return document;
    }

    private async Task RestoreQuerierAssignmentAsync(
        Guid workItemId,
        WorkItem? current,
        WorkItemAuditEntry queryEntry,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (current is null || !string.IsNullOrWhiteSpace(current.AssignedToId))
            {
                return;
            }

            var querierId = queryEntry.CreatedBy;
            if (string.IsNullOrWhiteSpace(querierId))
            {
                logger.LogWarning(
                    "Work item {WorkItemId} resumed from query while unassigned, but the query audit entry records no raising user.",
                    workItemId
                );
                return;
            }

            // The resume's post-action hooks write to the same document, some
            // on the background queue, so the assignment can lose the
            // optimistic-concurrency race: retry, re-reading each time.
            for (var attempt = 1; attempt <= MaxAssignAttempts; attempt++)
            {
                var assignResult = await engine.AssignAsync(
                    workItemId,
                    querierId,
                    queryEntry.CreatedByName,
                    user,
                    cancellationToken
                );
                if (assignResult.IsSuccess || assignResult.IsIdempotentReplay)
                {
                    return;
                }

                if (assignResult.FailureCode != WorkItemActionFailureCode.ConcurrencyConflict || attempt == MaxAssignAttempts)
                {
                    logger.LogError(
                        "Work item {WorkItemId} resumed from query but could not be re-assigned to {UserId} ({FailureCode}); it remains unassigned.",
                        workItemId,
                        querierId,
                        assignResult.FailureCode
                    );
                    return;
                }

                await Task.Delay(s_assignRetryDelay * attempt, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Work item {WorkItemId} resumed from query but its assignment could not be restored.", workItemId);
        }
    }
}
