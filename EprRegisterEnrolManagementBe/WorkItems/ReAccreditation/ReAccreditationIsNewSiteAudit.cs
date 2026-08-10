using EprRegisterEnrolManagementBe.Utils.Mongo;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// READ-ONLY diagnostic for epr-2uxy: sizes and classifies the population of
/// work items whose frozen <c>payload.overseasSites.sites[].isNewSite</c> may
/// carry a wrongly defaulted <c>true</c>.
///
/// <para>
/// <strong>The defect.</strong> Operator-side <c>OverseasSiteModel</c> gained
/// <c>IsNewSite</c> with a <c>= true</c> property initializer (423d27d,
/// 2026-07-26). Applications are POCO-mapped into Mongo, and a <em>missing</em>
/// BSON element leaves the C# initializer rather than <c>default(bool)</c> — so
/// any overseas site persisted before that date reads back as <c>true</c>
/// whatever it actually was. From 9e8e9da (2026-07-26, RA-294) that value began
/// being transmitted here, where it is frozen onto the work item and never
/// re-derived. RA-292 did not create this; it made it regulator-visible by
/// rendering a "New" badge from the field.
/// </para>
///
/// <para>
/// <strong>The discriminator (established by the operator-backend owner).</strong>
/// ReEx-sourced sites have never carried an <c>orsId</c>:
/// <c>HttpReExApiAdapter.MapOverseasSite</c> has never set it in any revision,
/// and it sets <c>IsNewSite = false</c> explicitly. Operator-added sites always
/// carry one — <c>AddOverseasSiteRequest.OrsId</c> is <c>required</c> and
/// validated <c>NotEmpty</c>. The operator serialiser uses
/// <c>WhenWritingNull</c>, so a null <c>orsId</c> is omitted rather than sent as
/// null. Therefore, within the window:
/// </para>
/// <list type="bullet">
///   <item><c>orsId</c> present ⟹ operator-added ⟹ <c>isNewSite: true</c> is
///   CORRECT.</item>
///   <item><c>orsId</c> absent ⟹ ReEx-sourced ⟹ <c>isNewSite: true</c> is
///   PROVABLY wrong (should be <c>false</c>).</item>
/// </list>
/// <para>
/// Crucially <c>orsId</c> entered the payload in 9e8e9da — the same commit that
/// started transmitting <c>isNewSite</c> — so there is no sub-window carrying
/// the bad flag without the signal.
/// </para>
///
/// <para>
/// <strong>The ambiguity guard.</strong> <c>orsId</c> is itself
/// client-clobberable (it is <c>string?</c> on the operator model, and
/// <c>PatchOverseasSites</c> replaced the site list wholesale). If some client
/// ever stripped it, an operator-added site would masquerade as ReEx-sourced and
/// a naive correction would stamp <c>false</c> over a genuinely new site —
/// hiding it from the regulator, which is the precise outcome worth more than
/// the defect itself. So a site missing <c>orsId</c> is only called provably
/// corrupt when it <em>also</em> carries none of the operator-entered detail
/// fields a ReEx-mapped site never has (contact details, operation code, waste
/// codes, address lines, BES evidence, interim site). A site missing
/// <c>orsId</c> that <em>does</em> carry such detail is reported separately as
/// AMBIGUOUS and must be adjudicated by hand, never auto-corrected.
/// </para>
///
/// <para>
/// <strong>Not affected, and deliberately not scanned.</strong>
/// <c>interimSite.isNewSite</c> — <c>InterimSiteModel</c> and its flag were
/// added in the same commit, so no interim site predates the flag, and ReEx
/// never creates them. <c>prns.authorisers[].isNew</c> — introduced by RA-292
/// itself and never previously transmitted. The entire remediation surface is
/// ORS-level <c>isNewSite</c>.
/// </para>
///
/// <para>
/// <strong>Why this lives in the app rather than only in a script.</strong>
/// There is a companion mongosh script at
/// <c>docs/diagnostics/ra292-isnewsite-audit.js</c> which is fine locally, but
/// CDP gives no way to run ad-hoc mongosh against a deployed database — the same
/// constraint that produced <see cref="Utils.StartupMigrationRunner"/>. Since
/// the open question on epr-2uxy is precisely "did any <em>deployed</em>
/// environment retain affected data", the count has to be takeable from inside
/// the app.
/// </para>
///
/// <para>
/// <strong>Read-only.</strong> This type calls no write API — only
/// <see cref="IMongoCollection{TDocument}"/> reads. That is a deliberate, tested
/// property: see <c>ReAccreditationIsNewSiteAuditTests</c>, which substitutes the
/// collection and asserts no mutating method is ever invoked.
/// </para>
///
/// <para>
/// Off by default; enable per environment with
/// <c>Diagnostics:Ra292IsNewSiteAudit=true</c>, boot, read the log, turn it off.
/// See <c>docs/diagnostics/ra292-isnewsite-audit.md</c>.
/// </para>
/// </summary>
internal static class ReAccreditationIsNewSiteAudit
{
    /// <summary>
    /// Configuration flag gating the diagnostic. Absent/false means the whole
    /// thing is a no-op, so it costs a deployed environment nothing to carry.
    /// </summary>
    public const string EnabledConfigKey = "Diagnostics:Ra292IsNewSiteAudit";

    /// <summary>
    /// Optional upper window bound (the RA-292 deploy time). Defaults to now,
    /// which over-reports rather than under-reports.
    /// </summary>
    public const string DeployedAtConfigKey = "Diagnostics:Ra292DeployedAt";

    /// <summary>
    /// Start of the at-risk window: 9e8e9da (2026-07-26, RA-294), the first
    /// build that transmitted <c>isNewSite</c> at all. A work item submitted
    /// before this carries no <c>isNewSite</c> anywhere, renders no badge, and
    /// is not at risk. The date bound is the PRIMARY filter.
    /// </summary>
    public static readonly DateTime WindowStart = new(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Detail fields that <c>HttpReExApiAdapter.MapOverseasSite</c> never
    /// populates. Their presence on a site missing <c>orsId</c> means the site
    /// was almost certainly operator-entered with its <c>orsId</c> stripped,
    /// rather than ReEx-sourced — so it must not be auto-corrected.
    /// </summary>
    private static readonly string[] s_operatorEnteredDetailFields =
    [
        "contactName", "contactEmail", "contactPhone",
        "operationCode", "code1", "code2", "code3",
        "addressLine1", "addressLine2", "townOrCity", "coordinates",
        "repatriatedLoads", "conditionsOfExport", "besEvidence", "interimSite"
    ];

    /// <summary>How one site's <c>isNewSite</c> value classifies.</summary>
    internal enum SiteVerdict
    {
        /// <summary><c>isNewSite</c> is false or absent — nothing to do.</summary>
        NotFlaggedNew,

        /// <summary>
        /// <c>isNewSite: true</c> with an <c>orsId</c> — operator-added, so the
        /// flag is correct.
        /// </summary>
        OperatorAddedCorrect,

        /// <summary>
        /// <c>isNewSite: true</c>, no <c>orsId</c>, and no operator-entered
        /// detail — ReEx-sourced, so the flag is provably wrong.
        /// </summary>
        ProvablyCorrupt,

        /// <summary>
        /// <c>isNewSite: true</c>, no <c>orsId</c>, but carrying
        /// operator-entered detail — <c>orsId</c> may have been stripped.
        /// Adjudicate by hand; never auto-correct.
        /// </summary>
        AmbiguousOrsIdMissing
    }

    internal sealed record SiteRow(int Index, string SiteName, SiteVerdict Verdict);

    internal sealed record AuditRow(
        string Id,
        string ApplicationReference,
        string OrganisationName,
        IReadOnlyList<SiteRow> Sites)
    {
        public int ProvablyCorruptCount =>
            Sites.Count(s => s.Verdict == SiteVerdict.ProvablyCorrupt);

        public int AmbiguousCount =>
            Sites.Count(s => s.Verdict == SiteVerdict.AmbiguousOrsIdMissing);
    }

    internal sealed record AuditResult(
        int ItemsScanned,
        IReadOnlyList<AuditRow> ItemsWithProvablyCorrupt,
        IReadOnlyList<AuditRow> ItemsWithAmbiguous,
        int SitesProvablyCorrupt,
        int SitesAmbiguous,
        int SitesOperatorAddedCorrect,
        int SitesNotFlaggedNew);

    /// <summary>
    /// Classify one site. Split out so the discriminator — the only part of this
    /// whose correctness decides whether a regulator sees a genuinely new site —
    /// is directly testable.
    /// </summary>
    internal static SiteVerdict ClassifySite(BsonDocument site)
    {
        ArgumentNullException.ThrowIfNull(site);

        var flaggedNew = site.TryGetValue("isNewSite", out var flag)
            && flag.IsBoolean
            && flag.AsBoolean;
        if (!flaggedNew)
        {
            return SiteVerdict.NotFlaggedNew;
        }

        // WhenWritingNull on the operator side means a null orsId is omitted,
        // so "present and non-empty" is the operator-added signal.
        var hasOrsId = site.TryGetValue("orsId", out var orsId)
            && !orsId.IsBsonNull
            && !string.IsNullOrWhiteSpace(orsId.ToString());
        if (hasOrsId)
        {
            return SiteVerdict.OperatorAddedCorrect;
        }

        var hasOperatorDetail = s_operatorEnteredDetailFields.Any(field =>
            site.TryGetValue(field, out var value) && !value.IsBsonNull);

        return hasOperatorDetail
            ? SiteVerdict.AmbiguousOrsIdMissing
            : SiteVerdict.ProvablyCorrupt;
    }

    /// <summary>
    /// Pure classification, split from the IO so the rules are testable without
    /// a database.
    /// </summary>
    internal static AuditResult Classify(IEnumerable<BsonDocument> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var withCorrupt = new List<AuditRow>();
        var withAmbiguous = new List<AuditRow>();
        var scanned = 0;
        int corruptSites = 0, ambiguousSites = 0, correctSites = 0, notNewSites = 0;

        foreach (var item in items)
        {
            scanned++;
            var sites = ReadSites(item);
            var rows = new List<SiteRow>(sites.Count);

            for (var i = 0; i < sites.Count; i++)
            {
                var verdict = ClassifySite(sites[i]);
                switch (verdict)
                {
                    case SiteVerdict.ProvablyCorrupt: corruptSites++; break;
                    case SiteVerdict.AmbiguousOrsIdMissing: ambiguousSites++; break;
                    case SiteVerdict.OperatorAddedCorrect: correctSites++; break;
                    default: notNewSites++; break;
                }

                rows.Add(new SiteRow(i, ReadString(sites[i], "siteName"), verdict));
            }

            var row = new AuditRow(
                Id: ReadString(item, "_id"),
                ApplicationReference: ReadString(item, "payload", "applicationReference"),
                OrganisationName: ReadString(item, "payload", "organisationName"),
                Sites: rows);

            if (row.ProvablyCorruptCount > 0)
            {
                withCorrupt.Add(row);
            }

            if (row.AmbiguousCount > 0)
            {
                withAmbiguous.Add(row);
            }
        }

        return new AuditResult(
            scanned, withCorrupt, withAmbiguous,
            corruptSites, ambiguousSites, correctSites, notNewSites);
    }

    /// <summary>
    /// <see cref="Utils.StartupMigrationRunner.StartupMigration"/>-shaped entry
    /// point. Despite the delegate's name this writes nothing — it is registered
    /// on that harness only because the harness is this service's one sanctioned
    /// way to run something once per environment against a deployed database.
    /// </summary>
    public static async Task RunAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        var configuration = services.GetRequiredService<IConfiguration>();
        if (!configuration.GetValue(EnabledConfigKey, false))
        {
            return;
        }

        var collection = services
            .GetRequiredService<IMongoDbClientFactory>()
            .GetCollection<BsonDocument>("workItems");

        var windowEnd = configuration.GetValue<DateTime?>(DeployedAtConfigKey) ?? DateTime.UtcNow;

        await RunAsync(collection, logger, windowEnd, cancellationToken);
    }

    /// <summary>
    /// Collection-level overload, so tests can substitute the collection and
    /// assert that nothing but a read is ever called on it.
    /// </summary>
    internal static async Task<AuditResult> RunAsync(
        IMongoCollection<BsonDocument> collection,
        ILogger logger,
        DateTime windowEnd,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(logger);

        var builder = Builders<BsonDocument>.Filter;
        var filter = builder.And(
            builder.Eq("typeId", ReAccreditationType.Id),
            builder.Gte("submittedAt", WindowStart),
            builder.Lt("submittedAt", windowEnd),
            builder.Exists("payload.overseasSites.sites.0"));

        var items = await collection.Find(filter).ToListAsync(cancellationToken);
        var result = Classify(items);

        logger.LogInformation(
            "epr-2uxy isNewSite audit (READ-ONLY, nothing written). Window {WindowStart:o} to " +
            "{WindowEnd:o}. Scanned {ItemsScanned} in-window re-accreditation work items " +
            "carrying overseas sites. Sites PROVABLY CORRUPT (isNewSite=true, no orsId, no " +
            "operator detail): {SitesProvablyCorrupt} across {ItemsWithProvablyCorrupt} items. " +
            "Sites AMBIGUOUS (no orsId but operator detail present — DO NOT auto-correct): " +
            "{SitesAmbiguous} across {ItemsWithAmbiguous} items. Sites correctly new " +
            "(operator-added, orsId present): {SitesOperatorAddedCorrect}. Sites not flagged " +
            "new: {SitesNotFlaggedNew}.",
            WindowStart, windowEnd, result.ItemsScanned,
            result.SitesProvablyCorrupt, result.ItemsWithProvablyCorrupt.Count,
            result.SitesAmbiguous, result.ItemsWithAmbiguous.Count,
            result.SitesOperatorAddedCorrect, result.SitesNotFlaggedNew);

        if (result.SitesProvablyCorrupt == 0 && result.SitesAmbiguous == 0)
        {
            logger.LogInformation(
                "epr-2uxy isNewSite audit: nothing at risk in this environment; record the " +
                "figure on the issue.");
            return result;
        }

        foreach (var row in result.ItemsWithProvablyCorrupt)
        {
            logger.LogInformation(
                "epr-2uxy PROVABLY CORRUPT {WorkItemId} ref={ApplicationReference} " +
                "org={OrganisationName} sites={SiteDetail}",
                row.Id, row.ApplicationReference, row.OrganisationName, Describe(row));
        }

        foreach (var row in result.ItemsWithAmbiguous)
        {
            // Warning rather than Information: this is the set where a careless
            // correction would hide a genuinely new site from the regulator.
            logger.LogWarning(
                "epr-2uxy AMBIGUOUS — orsId missing but operator-entered detail present, so " +
                "this may be an operator-added site whose orsId was stripped rather than a " +
                "ReEx-sourced one. Adjudicate by hand against the operator database; do NOT " +
                "auto-correct. {WorkItemId} ref={ApplicationReference} org={OrganisationName} " +
                "sites={SiteDetail}",
                row.Id, row.ApplicationReference, row.OrganisationName, Describe(row));
        }

        return result;
    }

    private static string Describe(AuditRow row) =>
        string.Join(" | ", row.Sites.Select(s => $"[{s.Index}] {s.SiteName} => {s.Verdict}"));

    private static List<BsonDocument> ReadSites(BsonDocument item)
    {
        if (!item.TryGetValue("payload", out var payload) || !payload.IsBsonDocument ||
            !payload.AsBsonDocument.TryGetValue("overseasSites", out var overseas) ||
            !overseas.IsBsonDocument ||
            !overseas.AsBsonDocument.TryGetValue("sites", out var sites) || !sites.IsBsonArray)
        {
            return [];
        }

        return [.. sites.AsBsonArray.Where(s => s.IsBsonDocument).Select(s => s.AsBsonDocument)];
    }

    // BsonValue.ToString() never returns null (it overrides object.ToString(),
    // which is declared string?), so the null-forgiving operator here removes an
    // unreachable branch rather than hiding a real one. The "(none)" fallback
    // covers the case that actually occurs: the key being absent.
    private static string ReadString(BsonDocument doc, string key) =>
        doc.TryGetValue(key, out var value) && !value.IsBsonNull
            ? value.ToString()!
            : "(none)";

    private static string ReadString(BsonDocument doc, string outerKey, string key) =>
        doc.TryGetValue(outerKey, out var outer) && outer.IsBsonDocument
            ? ReadString(outer.AsBsonDocument, key)
            : "(none)";
}
