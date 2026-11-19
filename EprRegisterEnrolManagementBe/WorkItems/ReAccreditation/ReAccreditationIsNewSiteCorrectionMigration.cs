using System.Globalization;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-794 remediation: corrects work-item
/// <c>payload.overseasSites.sites[].isNewSite</c> values that are
/// <em>provably</em> a wrongly-defaulted <c>true</c>, and refuses to touch
/// anything else. Run it only after the operator-side correction in
/// epr-register-enrol-backend: a query → resubmit overwrites
/// <c>payload.overseasSites</c> with the operator database's copy
/// (<c>ReAccreditationResumeService</c>), which would undo a correction made
/// here first.
///
/// <para>
/// See <see cref="ReAccreditationIsNewSiteAudit"/> for the defect, the
/// <c>orsId</c> discriminator and the ambiguity guard — this migration reuses
/// <see cref="ReAccreditationIsNewSiteAudit.ClassifySite"/> verbatim rather than
/// re-deriving the rule, so the thing that decides whether a regulator sees a
/// genuinely new site has exactly one implementation. That discriminator is
/// only valid up to epr-register-enrol-backend's RA-507 (2026-08-27), after
/// which ReEx-sourced sites carry a real <c>orsId</c> too — see the audit's
/// docs for why the fixed window still makes this safe, and why <c>windowEnd</c>
/// must stay pinned before that date on any future run.
/// </para>
///
/// <para>
/// <strong>Why this is gated so heavily.</strong> The failure mode is
/// asymmetric. The current defect errs toward <em>over</em>-showing: a spurious
/// "New" badge is visible, questionable and traceable. A bad correction errs
/// toward <em>under</em>-showing: it stamps a fabricated <c>false</c> over a
/// genuinely new site and hides it from regulator scrutiny, silently and
/// untraceably. That is strictly worse than the defect being fixed, so this must
/// not be capable of running on analysis alone — someone has to have looked at
/// real records first.
/// </para>
///
/// <para><strong>Five gates, all required before a single write:</strong></para>
/// <list type="number">
///   <item><see cref="EnabledConfigKey"/> — off by default; the migration is a
///   no-op in every environment until deliberately switched on.</item>
///   <item><see cref="SpotCheckConfirmedByConfigKey"/> — must name the human who
///   spot-checked the <c>orsId</c>-absent classification against the operator
///   database. Recorded in the audit entry, so the authorisation is auditable
///   rather than a shell variable that vanishes. This is the gate that makes
///   "constructible" ≠ "safe to run unreviewed".</item>
///   <item><see cref="OperatorRefusedSitesConfigKey"/> — the sites the
///   operator-side correction refused, carried across from its final APPLY run,
///   or <see cref="OperatorRefusedNothing"/>. The operator classifier is stricter
///   than this one: it refuses a no-<c>orsId</c> site unless ReEx itself proves
///   the site, because such a site may be genuinely new. Clearing the flag here
///   on a site the operator side refused would hide it from the regulator, and
///   the next resubmit would put <c>true</c> back anyway. There is no default:
///   absent, blank or malformed, nothing runs. And APPLY writes nothing while
///   any listed site matches no site here, since that may be a mistyped id
///   leaving the real site unexcluded.</item>
///   <item><see cref="ApplyConfigKey"/> — DRY RUN unless explicitly true. The
///   default run reports precisely what it would change and writes nothing.</item>
///   <item>Per site, only the two ReEx-sourced correctable verdicts
///   (<see cref="ReAccreditationIsNewSiteAudit.SiteVerdict.ProvablyCorrupt"/> and
///   <see cref="ReAccreditationIsNewSiteAudit.SiteVerdict.PromotedCorrectable"/>),
///   and of those only a site whose (<c>payload.operatorApplicationId</c>,
///   <c>siteId</c>) the operator side did not refuse. A site with no
///   <c>orsId</c> that was never promoted yet carries detail nothing accounts
///   for is <em>refused</em> in code, not left to a human to notice — and the
///   refusal names the fields that caused it. So is a correctable site that
///   cannot be matched against the operator refusals because either id is
///   missing.</item>
/// </list>
///
/// <para>
/// Idempotent: a corrected site reads <c>isNewSite: false</c> and therefore
/// classifies as <c>NotFlaggedNew</c> on any subsequent run.
/// </para>
///
/// <para>
/// Scope is ORS-level <c>isNewSite</c> only. Interim sites cannot be affected
/// (<c>InterimSiteModel</c> and its flag were added in the same commit, and ReEx
/// never creates interim sites); authoriser <c>isNew</c> was introduced by
/// RA-292 and never previously transmitted. Neither is touched.
/// </para>
/// </summary>
internal sealed class ReAccreditationIsNewSiteCorrectionMigration(
    IConfiguration configuration,
    ILogger<ReAccreditationIsNewSiteCorrectionMigration> logger,
    TimeProvider? timeProvider = null
) : IWorkItemMigration
{
    public const string EnabledConfigKey = "Diagnostics:Ra292CorrectIsNewSite";
    public const string SpotCheckConfirmedByConfigKey = "Diagnostics:Ra292SpotCheckConfirmedBy";
    public const string ApplyConfigKey = "Diagnostics:Ra292CorrectIsNewSiteApply";

    /// <summary>
    /// The sites the operator-side correction (epr-register-enrol-backend
    /// <c>IsNewSiteCorrectionMigration</c>) refused, as a comma-separated list of
    /// <c>&lt;ApplicationId&gt;:&lt;SiteId&gt;</c> pairs, one per
    /// <c>RA-794 REFUSED to correct application {ApplicationId} site {SiteId}</c>
    /// line of its final APPLY run. <c>ApplicationId</c> is that application's
    /// 24-hex ObjectId, which the operator backend sends as this payload's
    /// <c>operatorApplicationId</c>; <c>SiteId</c> is its integer
    /// <c>OverseasSiteModel.SiteId</c>, sent as <c>overseasSites.sites[].siteId</c>.
    /// When it refused nothing, the value is <see cref="OperatorRefusedNothing"/>,
    /// stated explicitly: an absent or blank value means the list was not carried
    /// across, not that it was empty.
    /// </summary>
    public const string OperatorRefusedSitesConfigKey = "Diagnostics:Ra292OperatorRefusedSites";

    /// <summary>The <see cref="OperatorRefusedSitesConfigKey"/> value stating the operator side refused no site.</summary>
    public const string OperatorRefusedNothing = "none";

    public const string AuditAction = "is-new-site-corrected";
    public const string AuditActionDisplayName = "Overseas site 'new' flag corrected";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string Name =>
        "ReAccreditation: correct provably-defaulted overseasSites isNewSite (RA-794)";

    public async Task ApplyAsync(
        IWorkItemPersistence persistence,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(persistence);

        if (ReadGates() is not { } gates)
        {
            return;
        }

        logger.LogInformation(
            "RA-794 correction starting. Mode={Mode}. Spot-check confirmed by "
                + "{ConfirmedBy}. Window {WindowStart:o} to {WindowEnd:o}. Sites the operator-side "
                + "correction refused: {OperatorRefusedCount}.",
            gates.Apply ? "APPLY" : "DRY RUN (nothing will be written)",
            gates.ConfirmedBy,
            ReAccreditationIsNewSiteAudit.WindowStart,
            gates.WindowEnd,
            gates.OperatorRefused.Count
        );

        // A listed operator refusal that matches no site here may be a mistyped
        // id, which would leave the real site unexcluded and let APPLY clear it.
        // So APPLY first plans every item without writing, and writes nothing
        // unless every listed site was found. A dry run reports regardless.
        if (gates.Apply)
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            await ForEachItemInWindowAsync(
                persistence,
                gates,
                full =>
                {
                    PlanCorrections(full, gates.OperatorRefused, found);
                    return Task.CompletedTask;
                },
                cancellationToken
            );

            if (LogUnmatchedOperatorRefusals(gates, found))
            {
                return;
            }
        }

        var tally = new Tally();
        await ForEachItemInWindowAsync(
            persistence,
            gates,
            full => CorrectOrReportAsync(persistence, full, gates, tally, cancellationToken),
            cancellationToken
        );

        if (!gates.Apply)
        {
            LogUnmatchedOperatorRefusals(gates, tally.OperatorRefusedFound);
        }

        // Each placeholder name appears exactly once. A repeated name is a
        // distinct positional slot to the structured-logging formatter, so
        // reusing one throws FormatException when the message is rendered —
        // and a test that logs through NullLogger never renders, so it would
        // pass while the real run produced no summary at all.
        logger.LogInformation(
            "RA-794 correction complete. Mode={Mode}. Items {ItemsVerb}: {ItemsChanged}. "
                + "Sites {SitesVerb}: {SitesCorrected}. Sites refused as ambiguous (need manual "
                + "adjudication): {SitesRefusedAmbiguous}. Sites refused because the operator side "
                + "refused them: {SitesRefusedByOperator}. Sites refused for want of an id to match: "
                + "{SitesRefusedUnmatchable}.",
            gates.Apply ? "APPLY" : "DRY RUN",
            gates.Apply ? "corrected" : "that would be corrected",
            tally.ItemsChanged,
            gates.Apply ? "corrected" : "that would be corrected",
            tally.SitesCorrected,
            tally.SitesRefusedAmbiguous,
            tally.SitesRefusedByOperator,
            tally.SitesRefusedUnmatchable
        );
    }

    /// <summary>Every gate's value, read once, after every gate has passed.</summary>
    private sealed record Gates(
        string ConfirmedBy,
        DateTime WindowEnd,
        HashSet<string> OperatorRefused,
        bool Apply
    );

    /// <summary>Running counts for the completion summary.</summary>
    private sealed class Tally
    {
        public int ItemsChanged { get; set; }
        public int SitesCorrected { get; set; }
        public int SitesRefusedAmbiguous { get; set; }
        public int SitesRefusedByOperator { get; set; }
        public int SitesRefusedUnmatchable { get; set; }
        public HashSet<string> OperatorRefusedFound { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Gates 1 to 4 in order, with the deploy time read once gates 1 and 2
    /// pass. Returns <c>null</c> when the migration must not run; every refusal
    /// but gate 1's (off, the normal state) has logged at Error.
    /// </summary>
    private Gates? ReadGates()
    {
        if (!configuration.GetValue(EnabledConfigKey, false))
        {
            return null;
        }

        var confirmedBy = configuration.GetValue<string?>(SpotCheckConfirmedByConfigKey);
        if (string.IsNullOrWhiteSpace(confirmedBy))
        {
            logger.LogError(
                "RA-794 correction is enabled but {SpotCheckKey} is not set, so it will not "
                    + "run. Set it to the name of whoever spot-checked the orsId-absent "
                    + "classification against the operator database. The check exists because orsId "
                    + "is itself client-clobberable: if a client ever stripped it, an operator-added "
                    + "site would masquerade as ReEx-sourced and this migration would hide a "
                    + "genuinely new site from the regulator.",
                SpotCheckConfirmedByConfigKey
            );
            return null;
        }

        if (ReAccreditationIsNewSiteAudit.ReadRequiredWindowEnd(configuration, logger) is not { } windowEnd)
        {
            return null;
        }

        if (ReadOperatorRefusedSites(configuration, logger) is not { } operatorRefused)
        {
            return null;
        }

        return new Gates(
            confirmedBy,
            windowEnd,
            operatorRefused,
            configuration.GetValue(ApplyConfigKey, false)
        );
    }

    /// <summary>
    /// Visits the full document of every re-accreditation work item submitted
    /// in the window, paging through all of them.
    /// </summary>
    private static async Task ForEachItemInWindowAsync(
        IWorkItemPersistence persistence,
        Gates gates,
        Func<WorkItem, Task> visit,
        CancellationToken cancellationToken
    )
    {
        var page = 1;
        while (true)
        {
            var result = await persistence.QueryAsync(
                new WorkItemQuery(
                    TypeIds: [ReAccreditationType.Id],
                    Page: page,
                    PageSize: WorkItemQuery.MaxPageSize,
                    IncludeArchived: true
                ),
                cancellationToken
            );

            foreach (var candidate in result.Items)
            {
                // The date bound is the primary filter. Applied here rather than
                // in the query so it stays visibly next to the reason for it.
                if (
                    candidate.SubmittedAt < ReAccreditationIsNewSiteAudit.WindowStart
                    || candidate.SubmittedAt >= gates.WindowEnd
                )
                {
                    continue;
                }

                // QueryAsync excludes Notes/AuditLog — fetch the full document
                // before mutating so a subsequent replace doesn't wipe them.
                if (await persistence.GetByIdAsync(candidate.Id, cancellationToken) is { } full)
                {
                    await visit(full);
                }
            }

            if (result.Items.Count < WorkItemQuery.MaxPageSize)
            {
                return;
            }

            page++;
        }
    }

    private async Task CorrectOrReportAsync(
        IWorkItemPersistence persistence,
        WorkItem full,
        Gates gates,
        Tally tally,
        CancellationToken cancellationToken
    )
    {
        var plan = PlanCorrections(full, gates.OperatorRefused, tally.OperatorRefusedFound);
        tally.SitesRefusedAmbiguous += plan.Ambiguous.Count;
        tally.SitesRefusedByOperator += plan.RefusedByOperator.Count;
        tally.SitesRefusedUnmatchable += plan.Unmatchable.Count;
        LogRefusals(full, plan);

        var corrected = plan.Corrections.Select(p => p.Label).ToList();
        if (corrected.Count == 0)
        {
            return;
        }

        tally.ItemsChanged++;
        tally.SitesCorrected += corrected.Count;

        if (!gates.Apply)
        {
            logger.LogInformation(
                "RA-794 DRY RUN would correct work item {WorkItemId} "
                    + "ref={ApplicationReference}: {Sites}",
                full.Id,
                full.Payload.GetValue("applicationReference", "(none)"),
                string.Join(", ", corrected)
            );
            return;
        }

        // Mutation happens here and nowhere else, so a dry run cannot
        // leave a half-corrected document behind even in memory.
        foreach (var (site, _) in plan.Corrections)
        {
            site["isNewSite"] = false;
        }

        full.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = AuditAction,
                ActionDisplayName = AuditActionDisplayName,
                CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                CreatedBy = "migration",
                CreatedByName = "Migration: RA-794 isNewSite correction",
                Details = new Dictionary<string, string?>
                {
                    ["issue"] = "RA-794",
                    ["reason"] =
                        "isNewSite was a wrongly-defaulted true; site has no orsId and no "
                        + "operator-entered detail, so it is ReEx-sourced and the correct "
                        + "value is false.",
                    ["sites"] = string.Join(", ", corrected),
                    ["spotCheckConfirmedBy"] = gates.ConfirmedBy,
                    ["operatorRefusedSitesListed"] = gates.OperatorRefused.Count.ToString(
                        CultureInfo.InvariantCulture
                    ),
                },
            }
        );

        await persistence.ReplaceAsync(full, cancellationToken);

        logger.LogInformation(
            "RA-794 corrected work item {WorkItemId}: {Sites}",
            full.Id,
            string.Join(", ", corrected)
        );
    }

    private void LogRefusals(WorkItem full, SitePlan plan)
    {
        foreach (var siteName in plan.Ambiguous)
        {
            logger.LogWarning(
                "RA-794 REFUSED to correct work item {WorkItemId} site '{SiteName}': "
                    + "orsId is missing but operator-entered detail is present, so this may "
                    + "be an operator-added site whose orsId was stripped rather than a "
                    + "ReEx-sourced one. Adjudicate by hand against the operator database.",
                full.Id,
                siteName
            );
        }

        foreach (var (label, refusedKey) in plan.RefusedByOperator)
        {
            logger.LogWarning(
                "RA-794 REFUSED to correct work item {WorkItemId} site {SiteLabel}: the "
                    + "operator-side correction refused {OperatorRefusedSite} (listed in "
                    + "{OperatorRefusedKey}), so it may be a genuinely new site. Never "
                    + "written; adjudicate by hand with the operator-backend owner.",
                full.Id,
                label,
                refusedKey,
                OperatorRefusedSitesConfigKey
            );
        }

        foreach (var (label, missing) in plan.Unmatchable)
        {
            logger.LogError(
                "RA-794 REFUSED to correct work item {WorkItemId} site {SiteLabel}: it has "
                    + "no usable {MissingId}, so it cannot be checked against the sites the "
                    + "operator-side correction refused ({OperatorRefusedKey}). Never written; "
                    + "adjudicate by hand with the operator-backend owner.",
                full.Id,
                label,
                missing,
                OperatorRefusedSitesConfigKey
            );
        }
    }

    /// <summary>
    /// Logs at Error every listed operator refusal that matched no site on any
    /// work item in the window, and returns whether there was any. Such a pair
    /// may be legitimate (the operator application never reached a work item in
    /// the window), but it is also what a mistyped id looks like, and a mistyped
    /// id leaves the real site unexcluded, so APPLY refuses while any remains.
    /// The confirmer removes a pair only after checking its application has no
    /// work item in the window.
    /// </summary>
    private bool LogUnmatchedOperatorRefusals(Gates gates, HashSet<string> found)
    {
        var unmatched = gates
            .OperatorRefused.Where(key => !found.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (unmatched.Count == 0)
        {
            return false;
        }

        foreach (var key in unmatched)
        {
            logger.LogError(
                "RA-794: {OperatorRefusedSite} is listed in {OperatorRefusedKey} but matched no "
                    + "site on any work item in the window. If it is mistyped, the real site is "
                    + "not excluded.",
                key,
                OperatorRefusedSitesConfigKey
            );
        }

        logger.LogError(
            "RA-794 correction: APPLY {ApplyOutcome}: {UnmatchedCount} site(s) listed in "
                + "{OperatorRefusedKey} matched no site on any work item in the window. Fix any "
                + "mistyped id. Remove a pair only after checking that its operator application has "
                + "no work item in the window, and record that on RA-794. Then run again.",
            gates.Apply ? "refused, nothing was written" : "would be refused",
            unmatched.Count,
            OperatorRefusedSitesConfigKey
        );
        return true;
    }

    /// <summary>
    /// Reads <see cref="OperatorRefusedSitesConfigKey"/> as a set of
    /// <c>&lt;applicationId&gt;:&lt;siteId&gt;</c> keys (application id
    /// lower-cased). Returns <c>null</c>, having logged at Error, when it is
    /// absent, blank or malformed; the caller must then refuse to run. Every
    /// entry is validated before any is used, so one bad entry cannot leave the
    /// rest half-applied, and <see cref="OperatorRefusedNothing"/> must stand
    /// alone.
    /// </summary>
    private static HashSet<string>? ReadOperatorRefusedSites(
        IConfiguration configuration,
        ILogger logger
    )
    {
        var raw = configuration[OperatorRefusedSitesConfigKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            logger.LogError(
                "RA-794 correction is enabled but {OperatorRefusedKey} is not set, so it will not "
                    + "run. Carry across every 'RA-794 REFUSED to correct application "
                    + "{{ApplicationId}} site {{SiteId}}' line from the operator-side correction's "
                    + "final APPLY run as <ApplicationId>:<SiteId>, comma-separated, or set it to "
                    + "'{OperatorRefusedNothing}' if that run refused no site. Without it this "
                    + "migration would clear the flag on sites the operator side refused because "
                    + "they may be genuinely new.",
                OperatorRefusedSitesConfigKey,
                OperatorRefusedNothing
            );
            return null;
        }

        var trimmed = raw.Trim();
        if (string.Equals(trimmed, OperatorRefusedNothing, StringComparison.OrdinalIgnoreCase))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in trimmed.Split(','))
        {
            if (TryParseRefusedSite(entry.Trim()) is not { } key)
            {
                logger.LogError(
                    "RA-794: {OperatorRefusedKey} entry '{MalformedEntry}' is not "
                        + "<ApplicationId>:<SiteId> (a 24-hex ObjectId and an integer site id), "
                        + "so nothing will run. Copy the ids exactly as the operator-side REFUSED "
                        + "lines log them; '{OperatorRefusedNothing}' must stand alone.",
                    OperatorRefusedSitesConfigKey,
                    entry.Trim(),
                    OperatorRefusedNothing
                );
                return null;
            }

            keys.Add(key);
        }

        return keys;
    }

    private static string? TryParseRefusedSite(string entry)
    {
        var parts = entry.Split(':');
        if (
            parts.Length != 2
            || !ObjectId.TryParse(parts[0].Trim(), out var applicationId)
            || !int.TryParse(
                parts[1].Trim(),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var siteId
            )
        )
        {
            return null;
        }

        return RefusedSiteKey(applicationId.ToString(), siteId);
    }

    private static string RefusedSiteKey(string applicationId, int siteId) =>
        $"{applicationId.ToLowerInvariant()}:{siteId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>What <see cref="PlanCorrections"/> decided for one work item's sites.</summary>
    private sealed record SitePlan(
        List<(BsonDocument Site, string Label)> Corrections,
        List<string> Ambiguous,
        List<(string Label, string RefusedKey)> RefusedByOperator,
        List<(string Label, string MissingId)> Unmatchable
    );

    /// <summary>
    /// Identify — without mutating — every provably-corrupt site on the item
    /// that the operator side did not refuse, returning the site documents to
    /// flip alongside a human-readable label, and separately the sites refused:
    /// as ambiguous (no <c>orsId</c> but operator-entered detail present), as
    /// refused by the operator side, or as unmatchable because the item has no
    /// <c>operatorApplicationId</c> or the site no integer <c>siteId</c>. Every
    /// listed operator refusal met on the item, whatever its verdict here, is
    /// added to <paramref name="operatorRefusedFound"/>.
    ///
    /// <para>
    /// Planning is deliberately separate from applying: a dry run must leave the
    /// document untouched even in memory, so that the report it produces
    /// describes the current state rather than a state it already half-created.
    /// </para>
    /// </summary>
    private static SitePlan PlanCorrections(
        WorkItem item,
        HashSet<string> operatorRefused,
        HashSet<string> operatorRefusedFound
    )
    {
        var plan = new SitePlan([], [], [], []);
        if (SitesOf(item.Payload) is not { } sites)
        {
            return plan;
        }

        var applicationId = OperatorApplicationIdOf(item.Payload);
        var index = 0;
        foreach (var element in sites)
        {
            if (element.IsBsonDocument)
            {
                var site = element.AsBsonDocument;
                var refusedKey = RefusedSiteKeyOf(applicationId, site);
                var refusedByOperator = refusedKey is not null && operatorRefused.Contains(refusedKey);
                if (refusedByOperator)
                {
                    operatorRefusedFound.Add(refusedKey!);
                }

                PlanSite(plan, site, index, applicationId, refusedKey, refusedByOperator);
            }

            index++;
        }

        return plan;
    }

    private static void PlanSite(
        SitePlan plan,
        BsonDocument site,
        int index,
        string? applicationId,
        string? refusedKey,
        bool refusedByOperator
    )
    {
        // BsonValue.ToString() never returns null; the index fallback covers
        // the case that actually occurs, siteName being absent.
        var name =
            site.TryGetValue("siteName", out var siteName) && !siteName.IsBsonNull
                ? siteName.ToString()!
                : $"site[{index}]";
        var label = $"[{index}] {name}";

        switch (ReAccreditationIsNewSiteAudit.ClassifySite(site))
        {
            // Both correctable verdicts are ReEx-sourced. PromotedCorrectable
            // is not a weaker case than ProvablyCorrupt — a promoted site's
            // operator detail is explained by ApplyPromotedFields, and
            // promoted sites are the bulk of the affected population.
            case ReAccreditationIsNewSiteAudit.SiteVerdict.ProvablyCorrupt:
            case ReAccreditationIsNewSiteAudit.SiteVerdict.PromotedCorrectable:
                if (refusedKey is null)
                {
                    plan.Unmatchable.Add(
                        (label, applicationId is null ? "operatorApplicationId" : "siteId")
                    );
                }
                else if (refusedByOperator)
                {
                    plan.RefusedByOperator.Add((label, refusedKey));
                }
                else
                {
                    plan.Corrections.Add((site, label));
                }

                break;

            case ReAccreditationIsNewSiteAudit.SiteVerdict.AmbiguousRefused:
                plan.Ambiguous.Add(
                    $"{name} (refusedBecause: "
                        + $"{string.Join(", ", ReAccreditationIsNewSiteAudit.RefusalTriggers(site))})"
                );
                break;

            default:
                // OperatorAddedCorrect / NotFlaggedNew — leave alone.
                break;
        }
    }

    private static BsonArray? SitesOf(BsonDocument payload) =>
        payload.TryGetValue("overseasSites", out var overseas)
        && overseas.IsBsonDocument
        && overseas.AsBsonDocument.TryGetValue("sites", out var sites)
        && sites.IsBsonArray
            ? sites.AsBsonArray
            : null;

    /// <summary>
    /// The operator backend sends <c>operatorApplicationId</c> on every real
    /// submission (<see cref="Models.ReAccreditationPayload.OperatorApplicationId"/>).
    /// Its absence makes a site unmatchable, never unrefused.
    /// </summary>
    private static string? OperatorApplicationIdOf(BsonDocument payload) =>
        payload.TryGetValue("operatorApplicationId", out var value)
        && value.IsString
        && !string.IsNullOrWhiteSpace(value.AsString)
            ? value.AsString.Trim()
            : null;

    /// <summary>
    /// The site's key in the operator refusals, or <c>null</c> when it cannot
    /// have one. <c>OverseasSiteModel.SiteId</c> is an int on the operator
    /// backend, so a real <c>siteId</c> is a BSON whole number; anything else
    /// cannot be matched to an operator REFUSED line.
    /// </summary>
    private static string? RefusedSiteKeyOf(string? applicationId, BsonDocument site) =>
        applicationId is not null
        && site.TryGetValue("siteId", out var siteId)
        && BsonWholeNumber.TryGet(siteId, int.MinValue, int.MaxValue, out var id)
            ? RefusedSiteKey(applicationId, id)
            : null;
}
