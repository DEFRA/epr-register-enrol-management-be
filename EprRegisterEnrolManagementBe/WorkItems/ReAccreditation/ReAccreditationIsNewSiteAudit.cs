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
    /// Detail fields that neither <c>HttpReExApiAdapter.MapOverseasSite</c> nor
    /// promotion accounts for. Their presence on a site that has no
    /// <c>orsId</c> and was never promoted is unexplained, and the most likely
    /// explanation is an operator-added site whose <c>orsId</c> was stripped —
    /// so it must not be auto-corrected.
    ///
    /// <para>
    /// Two categories are deliberately NOT here, both verified against the
    /// operator endpoints rather than inferred:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>siteName</c>, <c>siteAddress</c>, <c>country</c>, <c>isEu</c>,
    ///   <c>isOecd</c>, <c>selected</c> — <c>MapOverseasSite</c> sets all of
    ///   these, so they are present on every ReEx site and tell us nothing.</item>
    ///   <item><c>besEvidence</c> and <c>interimSite</c> — these indicate
    ///   operator <em>activity on</em> a site, not operator <em>creation of</em>
    ///   one. <c>AddBesEvidenceFile</c> and <c>AddInterimSite</c> both resolve
    ///   the target by <c>SiteId</c> alone with no provenance guard, so an
    ///   operator can and routinely does attach both to a carried-over
    ///   ReEx site — uploading broadly-equivalent-standards evidence against a
    ///   prior-year site is the point of that journey. Including them would have
    ///   refused most of the population this exists to fix.</item>
    /// </list>
    /// </summary>
    private static readonly string[] s_unexplainedOperatorDetailFields =
    [
        "contactName", "contactEmail", "contactPhone",
        "operationCode", "code1", "code2", "code3",
        "addressLine1", "addressLine2", "townOrCity", "coordinates",
        "repatriatedLoads", "conditionsOfExport"
    ];

    /// <summary>
    /// How one site's <c>isNewSite</c> value classifies. The names are the
    /// bucket names used in the dry-run report, the runbook and epr-2uxy, so
    /// they stay in step.
    /// </summary>
    internal enum SiteVerdict
    {
        /// <summary><c>isNewSite</c> is false or absent — nothing to do.</summary>
        NotFlaggedNew,

        /// <summary>
        /// ALREADY-CORRECT. <c>isNewSite: true</c> with an <c>orsId</c> —
        /// operator-added, so the flag is genuine.
        /// </summary>
        OperatorAddedCorrect,

        /// <summary>
        /// PROVABLY-CORRUPT. <c>isNewSite: true</c>, no <c>orsId</c>, never
        /// promoted, and no unexplained detail — a clean ReEx-sourced site, so
        /// the flag is a defaulted value.
        /// </summary>
        ProvablyCorrupt,

        /// <summary>
        /// PROMOTED-CORRECTABLE. <c>isNewSite: true</c>, no <c>orsId</c>, and
        /// <c>registeredNowAccredited: true</c> — a promoted registered site.
        /// Its operator-detail fields were written by <c>ApplyPromotedFields</c>
        /// and are therefore explained, not evidence of operator creation.
        /// Correctable.
        /// </summary>
        PromotedCorrectable,

        /// <summary>
        /// AMBIGUOUS-REFUSED. <c>isNewSite: true</c>, no <c>orsId</c>, never
        /// promoted, yet carrying detail nothing accounts for — a stripped
        /// <c>orsId</c> on an operator-added site is a live possibility.
        /// Adjudicate by hand; never auto-correct.
        /// </summary>
        AmbiguousRefused
    }

    /// <summary>
    /// One site's verdict. <see cref="RefusedBecause"/> is populated only for
    /// <see cref="SiteVerdict.AmbiguousRefused"/> and names the fields that
    /// caused the refusal.
    /// </summary>
    internal sealed record SiteRow(
        int Index,
        string SiteName,
        SiteVerdict Verdict,
        IReadOnlyList<string> RefusedBecause);

    internal sealed record AuditRow(
        string Id,
        string ApplicationReference,
        string OrganisationName,
        IReadOnlyList<SiteRow> Sites)
    {
        /// <summary>Sites this item contributes to the correctable buckets.</summary>
        public int CorrectableCount => Sites.Count(s =>
            s.Verdict is SiteVerdict.ProvablyCorrupt or SiteVerdict.PromotedCorrectable);

        public int AmbiguousCount =>
            Sites.Count(s => s.Verdict == SiteVerdict.AmbiguousRefused);
    }

    /// <summary>
    /// Tiered result. Counts are broken out per bucket rather than rolled into a
    /// single total: the buckets fail in different directions, and collapsing
    /// them would hide the one failure mode this classifier actually has — a
    /// miscalibrated tell inflating <see cref="SitesAmbiguousRefused"/> while
    /// looking like appropriate caution.
    /// </summary>
    internal sealed record AuditResult(
        int ItemsScanned,
        IReadOnlyList<AuditRow> ItemsWithCorrectable,
        IReadOnlyList<AuditRow> ItemsWithAmbiguous,
        int SitesProvablyCorrupt,
        int SitesPromotedCorrectable,
        int SitesAmbiguousRefused,
        int SitesAlreadyCorrect,
        int SitesNotFlaggedNew)
    {
        /// <summary>Everything the migration would act on.</summary>
        public int SitesCorrectable => SitesProvablyCorrupt + SitesPromotedCorrectable;
    }

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
        // so "present and non-empty" is the operator-added signal. Everything
        // past this point is ReEx-sourced BY CONSTRUCTION: an operator-added
        // site always carries an orsId and never reaches it. That is what makes
        // the promotion check below a safe positive resolver rather than a
        // second guess at provenance.
        var hasOrsId = site.TryGetValue("orsId", out var orsId)
            && !orsId.IsBsonNull
            && !string.IsNullOrWhiteSpace(orsId.ToString());
        if (hasOrsId)
        {
            return SiteVerdict.OperatorAddedCorrect;
        }

        // registeredNowAccredited is set only by PromoteOverseasSite, which runs
        // ApplyPromotedFields — and that writes the full operator-detail set
        // while never setting OrsId. So on a promoted site the detail is
        // EXPLAINED, not suspicious. Checking this before the detail check is
        // what stops the migration refusing the very population it exists to
        // fix: promoted sites are ReEx-sourced legacy sites, and a promoted one
        // trips the detail tell on thirteen fields at once.
        var promoted = site.TryGetValue("registeredNowAccredited", out var registered)
            && registered.IsBoolean
            && registered.AsBoolean;
        if (promoted)
        {
            return SiteVerdict.PromotedCorrectable;
        }

        return RefusalTriggers(site).Count > 0
            ? SiteVerdict.AmbiguousRefused
            : SiteVerdict.ProvablyCorrupt;
    }

    /// <summary>
    /// The unexplained detail fields present on a site — the reason a refusal
    /// happened.
    ///
    /// <para>
    /// Surfaced per record in the report on purpose. This classifier's failure
    /// mode is silent and in the safe direction: a miscalibration refuses
    /// records rather than corrupting them, so it shows up only as a large
    /// ambiguous bucket that reads as appropriate caution — and the natural
    /// conclusion, "the data is too messy to remediate", would close epr-2uxy as
    /// intractable when it is not. A reader who can see "refused because
    /// contactName, operationCode" against a promoted site can tell a
    /// miscalibrated classifier from genuinely messy data without reading any
    /// source. The first version of this classifier had exactly that defect.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> RefusalTriggers(BsonDocument site)
    {
        ArgumentNullException.ThrowIfNull(site);

        return
        [
            .. s_unexplainedOperatorDetailFields.Where(field =>
                site.TryGetValue(field, out var value) && !value.IsBsonNull)
        ];
    }

    /// <summary>
    /// Pure classification, split from the IO so the rules are testable without
    /// a database.
    /// </summary>
    internal static AuditResult Classify(IEnumerable<BsonDocument> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var withCorrectable = new List<AuditRow>();
        var withAmbiguous = new List<AuditRow>();
        var scanned = 0;
        int corruptSites = 0, promotedSites = 0, ambiguousSites = 0;
        int correctSites = 0, notNewSites = 0;

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
                    case SiteVerdict.PromotedCorrectable: promotedSites++; break;
                    case SiteVerdict.AmbiguousRefused: ambiguousSites++; break;
                    case SiteVerdict.OperatorAddedCorrect: correctSites++; break;
                    default: notNewSites++; break;
                }

                rows.Add(new SiteRow(
                    i,
                    ReadString(sites[i], "siteName"),
                    verdict,
                    verdict == SiteVerdict.AmbiguousRefused
                        ? RefusalTriggers(sites[i])
                        : []));
            }

            var row = new AuditRow(
                Id: ReadString(item, "_id"),
                ApplicationReference: ReadString(item, "payload", "applicationReference"),
                OrganisationName: ReadString(item, "payload", "organisationName"),
                Sites: rows);

            if (row.CorrectableCount > 0)
            {
                withCorrectable.Add(row);
            }

            if (row.AmbiguousCount > 0)
            {
                withAmbiguous.Add(row);
            }
        }

        return new AuditResult(
            scanned, withCorrectable, withAmbiguous,
            corruptSites, promotedSites, ambiguousSites, correctSites, notNewSites);
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

        // Explicit per-bucket counts rather than one total plus a list. The
        // buckets fail in different directions, and a single "at risk" number
        // would hide the failure this classifier actually has: a miscalibrated
        // tell inflating AMBIGUOUS-REFUSED while looking like caution.
        logger.LogInformation(
            "epr-2uxy isNewSite audit (READ-ONLY, nothing written). Window {WindowStart:o} to " +
            "{WindowEnd:o}. Scanned {ItemsScanned} in-window re-accreditation work items " +
            "carrying overseas sites. Buckets — " +
            "PROVABLY-CORRUPT (no orsId, unpromoted, no unexplained detail): {SitesProvablyCorrupt}; " +
            "PROMOTED-CORRECTABLE (no orsId, registeredNowAccredited=true): {SitesPromotedCorrectable}; " +
            "AMBIGUOUS-REFUSED (no orsId, unpromoted, unexplained detail): {SitesAmbiguousRefused}; " +
            "ALREADY-CORRECT (operator-added, orsId present): {SitesAlreadyCorrect}; " +
            "not flagged new: {SitesNotFlaggedNew}. " +
            // Each placeholder name appears exactly once: a repeated name is a
            // distinct positional slot to the structured-logging formatter, so
            // reusing one throws FormatException at render time — invisible to a
            // test that logs through NullLogger, which never formats.
            "Total correctable: {SitesCorrectable} sites across {ItemsWithCorrectable} items; " +
            "refused across {ItemsWithAmbiguous} items.",
            WindowStart, windowEnd, result.ItemsScanned,
            result.SitesProvablyCorrupt, result.SitesPromotedCorrectable,
            result.SitesAmbiguousRefused, result.SitesAlreadyCorrect, result.SitesNotFlaggedNew,
            result.SitesCorrectable, result.ItemsWithCorrectable.Count,
            result.ItemsWithAmbiguous.Count);

        if (result.SitesCorrectable == 0 && result.SitesAmbiguousRefused == 0)
        {
            logger.LogInformation(
                "epr-2uxy isNewSite audit: nothing at risk in this environment; record the " +
                "figure on the issue.");
            return result;
        }

        // A refused bucket that dwarfs the correctable one is far more likely to
        // mean the tell is miscalibrated than that the data is unremediable —
        // that is exactly how the first version of this classifier failed, by
        // refusing every promoted site. Say so at the point of observation
        // rather than leaving the reader to infer "too messy to fix".
        if (result.SitesAmbiguousRefused > result.SitesCorrectable)
        {
            logger.LogWarning(
                "epr-2uxy isNewSite audit: MORE sites were refused ({SitesAmbiguousRefused}) " +
                "than were classified correctable ({SitesCorrectable}). Treat this as a reason " +
                "to question the classifier before concluding the data cannot be remediated. " +
                "Check the refusedBecause fields below: if the same fields recur across many " +
                "records, the tell is probably over-broad rather than the data messy. See " +
                "docs/diagnostics/ra292-isnewsite-audit.md.",
                result.SitesAmbiguousRefused, result.SitesCorrectable);
        }

        foreach (var row in result.ItemsWithCorrectable)
        {
            logger.LogInformation(
                "epr-2uxy CORRECTABLE {WorkItemId} ref={ApplicationReference} " +
                "org={OrganisationName} sites={SiteDetail}",
                row.Id, row.ApplicationReference, row.OrganisationName, Describe(row));
        }

        foreach (var row in result.ItemsWithAmbiguous)
        {
            // Warning rather than Information: this is the set where a careless
            // correction would hide a genuinely new site from the regulator.
            logger.LogWarning(
                "epr-2uxy AMBIGUOUS-REFUSED — no orsId, never promoted, yet carrying detail " +
                "nothing accounts for, so this may be an operator-added site whose orsId was " +
                "stripped. Adjudicate by hand against the operator database; do NOT " +
                "auto-correct. {WorkItemId} ref={ApplicationReference} org={OrganisationName} " +
                "sites={SiteDetail}",
                row.Id, row.ApplicationReference, row.OrganisationName, Describe(row));
        }

        return result;
    }

    /// <summary>
    /// Render a row's per-site verdicts, naming the fields behind any refusal so
    /// a reader can distinguish a genuinely ambiguous record from a
    /// miscalibrated tell without reading the source.
    /// </summary>
    private static string Describe(AuditRow row) =>
        string.Join(" | ", row.Sites.Select(s => s.RefusedBecause.Count > 0
            ? $"[{s.Index}] {s.SiteName} => {s.Verdict} (refusedBecause: {string.Join(", ", s.RefusedBecause)})"
            : $"[{s.Index}] {s.SiteName} => {s.Verdict}"));

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
