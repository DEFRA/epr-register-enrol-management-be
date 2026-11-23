using System.Security.Claims;
using System.Text.Json;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-768 default <see cref="IReAccreditationOverseasSitesCorrectionService"/>.
///
/// Before RA-557, a query resubmission that dropped overseas sites reached only
/// <c>payload.latestSections</c>. <c>payload.overseasSites.sites</c>, which the
/// case management pages read, kept the dropped sites. This service removes
/// the named sites from that array and nothing else. It never replaces the
/// list from the operator backend, because a regulator may have changed a
/// remaining site's recycling operation codes here (RA-469).
///
/// A request entry matches stored sites by <c>orsId</c> when it has one, and by
/// <c>siteId</c> only when it does not. Ids are compared by their string form,
/// because the operator backend stores <c>siteId</c> as a number. Every stored
/// site an entry matches is removed. An entry that matches nothing is reported
/// as already absent. When nothing is removed, nothing is written.
///
/// A decided work item (see <see cref="ReAccreditationCorrectionGuards.IsDecided"/>)
/// or a duly-made one is never changed. On a single
/// <see cref="IWorkItemPersistence.ReplaceAsync"/>, the service writes the
/// edited <c>overseasSites</c> field and appends one
/// <c>overseas-sites-corrected</c> audit entry. That write is version-checked,
/// so a concurrent change is never overwritten. On a
/// <see cref="WorkItemConcurrencyException"/> it reloads and re-checks, as
/// <see cref="ReAccreditationChargeCorrectionService"/> does.
/// <see cref="WorkItem.StateId"/> and the SLA clock are not changed.
/// </summary>
internal sealed class ReAccreditationOverseasSitesCorrectionService(
    IWorkItemPersistence persistence,
    IWorkItemRegistry registry,
    TimeProvider timeProvider,
    ILogger<ReAccreditationOverseasSitesCorrectionService> logger
) : IReAccreditationOverseasSitesCorrectionService
{
    private const int MaxAttempts = 3;

    // A dedicated audit action, not `action-applied`: this is not a state
    // transition, and a transition-shaped entry would be mistaken for a resume
    // entry by StandardUpdatedOrigin.
    public const string AuditAction = "overseas-sites-corrected";
    public const string AuditActionDisplayName = "Overseas sites corrected";

    // WorkItemAuditEntry.Details is a string-to-string map, so the removed ids
    // are stored as a JSON-encoded array of { orsId, siteId }.
    public const string RemovedSitesDetailKey = "removedSites";
    public const string ReasonDetailKey = "reason";

    private const string OverseasSitesField = "overseasSites";
    private const string SitesField = "sites";
    private const string OrsIdField = "orsId";
    private const string SiteIdField = "siteId";

    private static readonly JsonSerializerOptions s_auditJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<OverseasSitesRemovalResult> RemoveSitesAsync(
        Guid workItemId,
        IReadOnlyList<OverseasSiteReference> sites,
        string reason,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(sites);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(user);

        if (string.IsNullOrWhiteSpace(user.FindFirstValue("user:id")))
        {
            return OverseasSitesRemovalResult.Refused(
                WorkItemActionFailureCode.MissingActorIdentity,
                "Mutating this work item requires an authenticated end user; "
                    + "the request did not include a 'user:id' claim."
            );
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);

            var refusal = CheckCorrectable(workItemId, workItem);
            if (refusal is not null)
            {
                return refusal;
            }

            var payload = workItem!.Payload.DeepClone().AsBsonDocument;
            var plan = Plan(workItemId, payload, sites);

            if (plan.Removed.Count == 0)
            {
                return new OverseasSitesRemovalResult { AlreadyAbsent = plan.AlreadyAbsent };
            }

            Apply(workItem, payload, plan, reason, user);

            try
            {
                await persistence.ReplaceAsync(workItem, cancellationToken);
            }
            catch (WorkItemConcurrencyException) when (attempt < MaxAttempts)
            {
                continue;
            }
            catch (WorkItemConcurrencyException ex)
            {
                logger.LogError(
                    ex,
                    "Overseas-sites removal on work item {WorkItemId} abandoned after {Attempts} attempts "
                        + "due to repeated concurrency conflicts.",
                    workItemId,
                    MaxAttempts
                );
                return OverseasSitesRemovalResult.Refused(
                    WorkItemActionFailureCode.ConcurrencyConflict,
                    $"Work item '{workItemId}' was modified concurrently. Reload the work item and retry.",
                    OverseasSitesRemovalResult.ConcurrencyConflictReason
                );
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Removed {RemovedCount} overseas site(s) {RemovedSites} from re-accreditation work item "
                        + "{WorkItemId} for {UserId}; {AlreadyAbsentCount} named site(s) were already absent.",
                    plan.Removed.Count,
                    Describe(plan.Removed),
                    workItemId,
                    user.FindFirstValue("user:id"),
                    plan.AlreadyAbsent.Count
                );
            }

            return new OverseasSitesRemovalResult
            {
                Removed = plan.Removed,
                AlreadyAbsent = plan.AlreadyAbsent,
            };
        }

        // Unreachable: the final attempt either returns or fails with a conflict.
        throw new InvalidOperationException("Overseas-sites removal loop exited unexpectedly.");
    }

    private OverseasSitesRemovalResult? CheckCorrectable(Guid workItemId, WorkItem? workItem)
    {
        if (workItem is null)
        {
            return OverseasSitesRemovalResult.Refused(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item has id '{workItemId}'."
            );
        }

        if (!string.Equals(workItem.TypeId, ReAccreditationType.Id, StringComparison.OrdinalIgnoreCase))
        {
            return OverseasSitesRemovalResult.Refused(
                WorkItemActionFailureCode.UnknownAction,
                $"Work item {workItemId} has type '{workItem.TypeId}', expected '{ReAccreditationType.Id}'."
            );
        }

        var template = WorkItemEngineRules.ResolveTemplate(workItem, registry);
        if (template is null)
        {
            return OverseasSitesRemovalResult.Refused(
                WorkItemActionFailureCode.UnknownAction,
                $"Work item {workItemId} has no registered type or template snapshot."
            );
        }

        // Decided first: a decided item is also duly made, and "decided" is the
        // stronger reason the business owner needs to see.
        if (ReAccreditationCorrectionGuards.IsDecided(template, workItem.StateId))
        {
            return Closed(
                workItemId,
                $"Work item {workItemId} is decided ('{workItem.StateId}'); its overseas sites cannot change.",
                OverseasSitesRemovalResult.DecidedReason
            );
        }

        return ReAccreditationCorrectionGuards.IsDulyMade(workItem)
            ? Closed(
                workItemId,
                $"Work item {workItemId} is duly made; its overseas sites cannot change.",
                OverseasSitesRemovalResult.DulyMadeReason
            )
            : null;
    }

    private OverseasSitesRemovalResult Closed(Guid workItemId, string message, string reason)
    {
        logger.LogInformation(
            "Overseas-sites removal refused for work item {WorkItemId}: {ConflictReason}.",
            workItemId,
            reason
        );
        return OverseasSitesRemovalResult.Refused(WorkItemActionFailureCode.TerminalState, message, reason);
    }

    private sealed record RemovalPlan(
        BsonArray? Sites,
        List<OverseasSiteReference> Removed,
        List<OverseasSiteReference> AlreadyAbsent
    );

    /// <summary>
    /// Works out which stored sites to remove, removing them from the
    /// <paramref name="payload"/> copy's <c>overseasSites.sites</c> array.
    /// A work item with no <c>overseasSites</c> or no <c>sites</c> holds no
    /// sites, so every entry is already absent. A field that is present with
    /// the wrong shape is corrupt data, and the removal fails loudly rather
    /// than guess.
    /// </summary>
    private RemovalPlan Plan(
        Guid workItemId,
        BsonDocument payload,
        IReadOnlyList<OverseasSiteReference> requested
    )
    {
        var removed = new List<OverseasSiteReference>();
        var alreadyAbsent = new List<OverseasSiteReference>();

        var storedSites = ReadStoredSites(workItemId, payload);
        if (storedSites is null)
        {
            alreadyAbsent.AddRange(requested);
            return new RemovalPlan(null, removed, alreadyAbsent);
        }

        foreach (var entry in requested)
        {
            var matches = storedSites
                .OfType<BsonDocument>()
                .Where(site => Matches(site, entry))
                .ToList();

            if (matches.Count == 0)
            {
                alreadyAbsent.Add(entry);
                continue;
            }

            foreach (var site in matches)
            {
                storedSites.Remove(site);
                removed.Add(new OverseasSiteReference(ReadId(site, OrsIdField), ReadId(site, SiteIdField)));
            }
        }

        return new RemovalPlan(storedSites, removed, alreadyAbsent);
    }

    private BsonArray? ReadStoredSites(Guid workItemId, BsonDocument payload)
    {
        if (!payload.TryGetValue(OverseasSitesField, out var overseasSitesValue) || overseasSitesValue.IsBsonNull)
        {
            return null;
        }

        if (overseasSitesValue is not BsonDocument overseasSites)
        {
            throw Corrupt(workItemId, $"payload.{OverseasSitesField} is a {overseasSitesValue.BsonType}, not a document");
        }

        if (!overseasSites.TryGetValue(SitesField, out var sitesValue) || sitesValue.IsBsonNull)
        {
            return null;
        }

        return sitesValue as BsonArray
            ?? throw Corrupt(
                workItemId,
                $"payload.{OverseasSitesField}.{SitesField} is a {sitesValue.BsonType}, not an array"
            );
    }

    private InvalidOperationException Corrupt(Guid workItemId, string problem)
    {
        logger.LogError(
            "Overseas-sites removal on work item {WorkItemId} failed: {Problem}.",
            workItemId,
            problem
        );
        return new InvalidOperationException($"Work item {workItemId}: {problem}.");
    }

    private static bool Matches(BsonDocument site, OverseasSiteReference entry) =>
        entry.OrsId is not null
            ? string.Equals(ReadId(site, OrsIdField), entry.OrsId, StringComparison.Ordinal)
            : entry.SiteId is not null
                && string.Equals(ReadId(site, SiteIdField), entry.SiteId, StringComparison.Ordinal);

    /// <summary>
    /// A stored id in its string form: <c>siteId</c> is a number in operator
    /// backend data and a string in some fixtures, and <c>ToString()</c> gives
    /// the plain value for both. Absent, null or blank reads as <c>null</c>.
    /// </summary>
    private static string? ReadId(BsonDocument site, string field)
    {
        if (!site.TryGetValue(field, out var value) || value.IsBsonNull)
        {
            return null;
        }

        var text = value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>
    /// Mutates the loaded instance in memory only; the caller commits it with
    /// the single <c>ReplaceAsync</c>. Only <c>overseasSites.sites</c> changes,
    /// and the remaining site documents are kept as they are.
    /// </summary>
    private void Apply(
        WorkItem workItem,
        BsonDocument payload,
        RemovalPlan plan,
        string reason,
        ClaimsPrincipal user
    )
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        payload[OverseasSitesField][SitesField] = plan.Sites!;
        workItem.ReplacePayload(payload);
        workItem.LastModifiedAt = now;

        workItem.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = AuditAction,
                ActionDisplayName = AuditActionDisplayName,
                Details = new Dictionary<string, string?>
                {
                    [RemovedSitesDetailKey] = JsonSerializer.Serialize(plan.Removed, s_auditJsonOptions),
                    [ReasonDetailKey] = reason,
                },
                CreatedAt = now,
                CreatedBy = user.FindFirstValue("user:id"),
                CreatedByName = user.FindFirstValue("user:name"),
                StateId = workItem.StateId,
            }
        );
    }

    private static string Describe(IEnumerable<OverseasSiteReference> sites) =>
        string.Join(", ", sites.Select(s => $"orsId={s.OrsId ?? "none"}/siteId={s.SiteId ?? "none"}"));
}
