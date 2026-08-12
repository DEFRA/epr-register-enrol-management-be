using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// epr-2uxy remediation: corrects frozen
/// <c>payload.overseasSites.sites[].isNewSite</c> values that are
/// <em>provably</em> a wrongly-defaulted <c>true</c>, and refuses to touch
/// anything else.
///
/// <para>
/// See <see cref="ReAccreditationIsNewSiteAudit"/> for the defect, the
/// <c>orsId</c> discriminator and the ambiguity guard — this migration reuses
/// <see cref="ReAccreditationIsNewSiteAudit.ClassifySite"/> verbatim rather than
/// re-deriving the rule, so the thing that decides whether a regulator sees a
/// genuinely new site has exactly one implementation.
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
/// <para><strong>Four gates, all required before a single write:</strong></para>
/// <list type="number">
///   <item><see cref="EnabledConfigKey"/> — off by default; the migration is a
///   no-op in every environment until deliberately switched on.</item>
///   <item><see cref="SpotCheckConfirmedByConfigKey"/> — must name the human who
///   spot-checked the <c>orsId</c>-absent classification against the operator
///   database. Recorded in the audit entry, so the authorisation is auditable
///   rather than a shell variable that vanishes. This is the gate that makes
///   "constructible" ≠ "safe to run unreviewed".</item>
///   <item><see cref="ApplyConfigKey"/> — DRY RUN unless explicitly true. The
///   default run reports precisely what it would change and writes nothing.</item>
///   <item>Per site, only the two ReEx-sourced correctable verdicts
///   (<see cref="ReAccreditationIsNewSiteAudit.SiteVerdict.ProvablyCorrupt"/> and
///   <see cref="ReAccreditationIsNewSiteAudit.SiteVerdict.PromotedCorrectable"/>).
///   A site with no <c>orsId</c> that was never promoted yet carries detail
///   nothing accounts for is <em>refused</em> in code, not left to a human to
///   notice — and the refusal names the fields that caused it.</item>
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
    TimeProvider? timeProvider = null) : IWorkItemMigration
{
    public const string EnabledConfigKey = "Diagnostics:Ra292CorrectIsNewSite";
    public const string SpotCheckConfirmedByConfigKey = "Diagnostics:Ra292SpotCheckConfirmedBy";
    public const string ApplyConfigKey = "Diagnostics:Ra292CorrectIsNewSiteApply";

    public const string AuditAction = "is-new-site-corrected";
    public const string AuditActionDisplayName = "Overseas site 'new' flag corrected";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string Name =>
        "ReAccreditation: correct provably-defaulted overseasSites isNewSite (epr-2uxy)";

    public async Task ApplyAsync(
        IWorkItemPersistence persistence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(persistence);

        if (!configuration.GetValue(EnabledConfigKey, false))
        {
            return;
        }

        var confirmedBy = configuration.GetValue<string?>(SpotCheckConfirmedByConfigKey);
        if (string.IsNullOrWhiteSpace(confirmedBy))
        {
            logger.LogError(
                "epr-2uxy correction is enabled but {SpotCheckKey} is not set, so it will not " +
                "run. Set it to the name of whoever spot-checked the orsId-absent " +
                "classification against the operator database. The check exists because orsId " +
                "is itself client-clobberable: if a client ever stripped it, an operator-added " +
                "site would masquerade as ReEx-sourced and this migration would hide a " +
                "genuinely new site from the regulator.",
                SpotCheckConfirmedByConfigKey);
            return;
        }

        var apply = configuration.GetValue(ApplyConfigKey, false);
        var windowEnd =
            configuration.GetValue<DateTime?>(ReAccreditationIsNewSiteAudit.DeployedAtConfigKey)
            ?? DateTime.UtcNow;

        logger.LogInformation(
            "epr-2uxy correction starting. Mode={Mode}. Spot-check confirmed by " +
            "{ConfirmedBy}. Window {WindowStart:o} to {WindowEnd:o}.",
            apply ? "APPLY" : "DRY RUN (nothing will be written)",
            confirmedBy, ReAccreditationIsNewSiteAudit.WindowStart, windowEnd);

        var itemsChanged = 0;
        var sitesCorrected = 0;
        var sitesRefusedAmbiguous = 0;
        var page = 1;

        while (true)
        {
            var result = await persistence.QueryAsync(
                new WorkItemQuery(
                    TypeIds: [ReAccreditationType.Id],
                    Page: page,
                    PageSize: WorkItemQuery.MaxPageSize,
                    IncludeArchived: true),
                cancellationToken);

            foreach (var candidate in result.Items)
            {
                // The date bound is the primary filter. Applied here rather than
                // in the query so it stays visibly next to the reason for it.
                if (candidate.SubmittedAt < ReAccreditationIsNewSiteAudit.WindowStart ||
                    candidate.SubmittedAt >= windowEnd)
                {
                    continue;
                }

                // QueryAsync excludes Notes/AuditLog — fetch the full document
                // before mutating so a subsequent replace doesn't wipe them.
                var full = await persistence.GetByIdAsync(candidate.Id, cancellationToken);
                if (full is null)
                {
                    continue;
                }

                var planned = PlanCorrections(full, out var refused);
                sitesRefusedAmbiguous += refused.Count;
                var corrected = planned.Select(p => p.Label).ToList();

                foreach (var siteName in refused)
                {
                    logger.LogWarning(
                        "epr-2uxy REFUSED to correct work item {WorkItemId} site '{SiteName}': " +
                        "orsId is missing but operator-entered detail is present, so this may " +
                        "be an operator-added site whose orsId was stripped rather than a " +
                        "ReEx-sourced one. Adjudicate by hand against the operator database.",
                        full.Id, siteName);
                }

                if (corrected.Count == 0)
                {
                    continue;
                }

                itemsChanged++;
                sitesCorrected += corrected.Count;

                if (!apply)
                {
                    logger.LogInformation(
                        "epr-2uxy DRY RUN would correct work item {WorkItemId} " +
                        "ref={ApplicationReference}: {Sites}",
                        full.Id, full.Payload.GetValue("applicationReference", "(none)"),
                        string.Join(", ", corrected));
                    continue;
                }

                // Mutation happens here and nowhere else, so a dry run cannot
                // leave a half-corrected document behind even in memory.
                foreach (var (site, _) in planned)
                {
                    site["isNewSite"] = false;
                }

                full.AuditLog.Add(new WorkItemAuditEntry
                {
                    Action = AuditAction,
                    ActionDisplayName = AuditActionDisplayName,
                    CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                    CreatedBy = "migration",
                    CreatedByName = "Migration: epr-2uxy isNewSite correction",
                    Details = new Dictionary<string, string?>
                    {
                        ["issue"] = "epr-2uxy",
                        ["reason"] =
                            "isNewSite was a wrongly-defaulted true; site has no orsId and no " +
                            "operator-entered detail, so it is ReEx-sourced and the correct " +
                            "value is false.",
                        ["sites"] = string.Join(", ", corrected),
                        ["spotCheckConfirmedBy"] = confirmedBy
                    }
                });

                await persistence.ReplaceAsync(full, cancellationToken);

                logger.LogInformation(
                    "epr-2uxy corrected work item {WorkItemId}: {Sites}",
                    full.Id, string.Join(", ", corrected));
            }

            if (result.Items.Count < WorkItemQuery.MaxPageSize)
            {
                break;
            }

            page++;
        }

        // Each placeholder name appears exactly once. A repeated name is a
        // distinct positional slot to the structured-logging formatter, so
        // reusing one throws FormatException when the message is rendered —
        // and a test that logs through NullLogger never renders, so it would
        // pass while the real run produced no summary at all.
        logger.LogInformation(
            "epr-2uxy correction complete. Mode={Mode}. Items {ItemsVerb}: {ItemsChanged}. " +
            "Sites {SitesVerb}: {SitesCorrected}. Sites refused as ambiguous (need manual " +
            "adjudication): {SitesRefusedAmbiguous}.",
            apply ? "APPLY" : "DRY RUN",
            apply ? "corrected" : "that would be corrected",
            itemsChanged,
            apply ? "corrected" : "that would be corrected",
            sitesCorrected,
            sitesRefusedAmbiguous);
    }

    /// <summary>
    /// Identify — without mutating — every provably-corrupt site on the item,
    /// returning the site documents to flip alongside a human-readable label,
    /// and separately the names of sites refused because their <c>orsId</c> is
    /// missing but operator-entered detail is present.
    ///
    /// <para>
    /// Planning is deliberately separate from applying: a dry run must leave the
    /// document untouched even in memory, so that the report it produces
    /// describes the current state rather than a state it already half-created.
    /// </para>
    /// </summary>
    private static List<(BsonDocument Site, string Label)> PlanCorrections(
        WorkItem item, out List<string> refused)
    {
        var planned = new List<(BsonDocument, string)>();
        refused = [];

        if (!item.Payload.TryGetValue("overseasSites", out var overseas) ||
            !overseas.IsBsonDocument ||
            !overseas.AsBsonDocument.TryGetValue("sites", out var sites) ||
            !sites.IsBsonArray)
        {
            return planned;
        }

        var index = 0;
        foreach (var element in sites.AsBsonArray)
        {
            if (!element.IsBsonDocument)
            {
                index++;
                continue;
            }

            var site = element.AsBsonDocument;
            // BsonValue.ToString() never returns null; the index fallback covers
            // the case that actually occurs, siteName being absent.
            var name = site.TryGetValue("siteName", out var siteName) && !siteName.IsBsonNull
                ? siteName.ToString()!
                : $"site[{index}]";

            switch (ReAccreditationIsNewSiteAudit.ClassifySite(site))
            {
                // Both correctable verdicts are ReEx-sourced. PromotedCorrectable
                // is not a weaker case than ProvablyCorrupt — a promoted site's
                // operator detail is explained by ApplyPromotedFields, and
                // promoted sites are the bulk of the affected population.
                case ReAccreditationIsNewSiteAudit.SiteVerdict.ProvablyCorrupt:
                case ReAccreditationIsNewSiteAudit.SiteVerdict.PromotedCorrectable:
                    planned.Add((site, $"[{index}] {name}"));
                    break;

                case ReAccreditationIsNewSiteAudit.SiteVerdict.AmbiguousRefused:
                    refused.Add(
                        $"{name} (refusedBecause: " +
                        $"{string.Join(", ", ReAccreditationIsNewSiteAudit.RefusalTriggers(site))})");
                    break;

                default:
                    // OperatorAddedCorrect / NotFlaggedNew — leave alone.
                    break;
            }

            index++;
        }

        return planned;
    }
}
