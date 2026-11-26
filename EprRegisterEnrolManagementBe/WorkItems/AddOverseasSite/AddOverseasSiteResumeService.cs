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
    /// left <c>queried</c> for a resume target succeeds as an idempotent replay;
    /// any other state is an
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

    /// <summary>The payload field the latest response is stamped under.</summary>
    public const string LatestResponsePayloadField = "latestQueryResponse";

    private const string InterimSitesField = "interimSites";

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
                logger.LogInformation(
                    "Resume-from-query for work item {WorkItemId} is a no-op: already in state '{StateId}'.",
                    workItemId,
                    workItem.StateId
                );
                return WorkItemActionResult.IdempotentReplay(workItem);
            }

            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item {workItemId} is in state '{workItem.StateId}' and cannot be resumed from a query."
            );
        }

        // The whole query audit entry, not just its action id: its CreatedBy
        // identifies the caseworker who queried, whom the item is re-assigned
        // to once the resume has landed.
        var queryEntry = workItem
            .AuditLog.Where(e => string.Equals(e.Action, AddOverseasSiteQueryService.AuditAction, StringComparison.Ordinal))
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefault(e => e.Details.GetValueOrDefault("actionId") is not null);

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
        if (unmatchedInterimSites.Count > 0)
        {
            logger.LogWarning(
                "Resume-from-query for work item {WorkItemId}: {Count} interim site change(s) matched no interim site on the payload and were recorded on the audit log only.",
                workItemId,
                unmatchedInterimSites.Count
            );
        }

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
        var appended = await auditAppender.AppendAsync(
            workItemId,
            action: AuditAction,
            actionDisplayName: AuditActionDisplayName,
            details: new Dictionary<string, string?>
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
                ["fileReferences"] = string.Join(";", besFiles.Select(f => $"bes-evidence:{f.FileId}:{f.Filename}")),
            },
            user,
            // The transition is committed: the audit record of what changed must be written regardless.
            CancellationToken.None
        );
        if (!appended)
        {
            logger.LogError(
                "Query-responded audit entry could not be appended to work item {WorkItemId} after action {ActionId} was applied.",
                workItemId,
                resumeActionId
            );
        }

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
            var previous = target.TryGetValue(field, out var existing) && existing.IsString ? existing.AsString : null;
            if (string.Equals(previous, value, StringComparison.Ordinal))
            {
                continue;
            }

            target[field] = value is null ? BsonNull.Value : new BsonString(value);
            applied[field] = new FieldChange(previous, value);
        }

        return applied;
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
