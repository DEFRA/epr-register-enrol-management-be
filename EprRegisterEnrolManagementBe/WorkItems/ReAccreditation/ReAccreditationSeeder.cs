using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Populates a fresh database with a small set of re-accreditation work
/// items so the case management UI has something to play with on first
/// boot. Only runs when the work item collection is empty (gated by
/// <see cref="WorkItemSeederHostedService"/>) so it is safe to leave
/// enabled in any environment.
///
/// Assignee ids match the stub-auth users in the frontend
/// (<c>stub-standard-1</c>, <c>stub-assign-1</c>) so the "My work items"
/// filter works immediately after a stub login.
///
/// RA-175: <see cref="INationResolver"/> is injected so the seeder calls
/// the same postcode-to-nation routing logic that
/// <see cref="ReAccreditationNationRoutingHook"/> applies to real
/// submissions. This ensures seeded items carry a correctly-derived
/// <c>payload.nation</c> value and appear under the right nation filter
/// in the work queue.
/// </summary>
internal sealed class ReAccreditationSeeder(INationResolver nationResolver) : IWorkItemSeeder
{
    private const string AccreditationYearKey = "accreditationYear";
    private const string AddressLine1Key = "addressLine1";
    private const string AddressLine2Key = "addressLine2";
    private const string BesEvidenceKey = "besEvidence";
    private const string ChargeAmountPenceKey = "chargeAmountPence";
    private const string CleanScanStatus = "Clean";
    private const string CompaniesHouseNumberKey = "companiesHouseNumber";
    private const string CompanyRegisterAddressPostcodeKey = "companyRegisterAddressPostcode";
    private const string ComplianceIssuesReportedKey = "complianceIssuesReported";
    private const string ContactEmailKey = "contactEmail";
    private const string ContactNameKey = "contactName";
    private const string ContactPhoneKey = "contactPhone";
    private const string ContentTypeKey = "contentType";
    private const string CountryKey = "country";
    private const string EmailKey = "email";
    private const string ExporterProcessingType = "exporter";
    private const string FileIdKey = "fileId";
    private const string FilenameKey = "filename";
    private const string FilesKey = "files";
    private const string FullNameKey = "fullName";
    private const string IsNewSiteKey = "isNewSite";
    private const string MaterialKey = "material";
    private const string OperatorApplicationIdKey = "operatorApplicationId";
    private const string OperatorEmailKey = "operatorEmail";
    private const string OrganisationNameKey = "organisationName";
    private const string OrsIdKey = "orsId";
    private const string PdfContentType = "application/pdf";
    private const string PlasticMaterial = "plastic";
    private const string PreviousAccreditationYearKey = "previousAccreditationYear";
    private const string RegistrationNumberKey = "registrationNumber";
    private const string S3BucketField = "s3Bucket";
    private const string S3KeyField = "s3Key";
    private const string ScanStatusKey = "scanStatus";
    private const string SiteAddressKey = "siteAddress";
    private const string SiteAddressPostcodeKey = "siteAddressPostcode";
    private const string SiteIdKey = "siteId";
    private const string SiteNameKey = "siteName";
    private const string StubAssigneeId = "stub-assign-1";
    private const string StubAssigneeName = "Stub Assign User";
    private const string StubPortalClientId = "stub-portal-client";
    private const string TownOrCityKey = "townOrCity";
    private const string UploadedAtKey = "uploadedAt";
    private const string WasteProcessingTypeKey = "wasteProcessingType";

    /// <summary>
    /// Sentinel <see cref="WorkItem.AssignedBy"/> value attributed to the
    /// seeder. Distinct from any real user id so the audit log makes the
    /// provenance of seeded assignments explicit and queryable. Setting
    /// <c>AssignedBy</c> to the assignee id (the original bug, epr-ce4)
    /// would falsify the audit trail to claim the assignee assigned
    /// themselves.
    /// </summary>
    public const string SeederAssignedBy = "system:seeder";

    /// <summary>
    /// The only seeded state that precedes <c>duly-made</c>, and therefore the
    /// only one that legitimately carries a null <see cref="WorkItem.SlaClock"/>
    /// (RA-295).
    /// </summary>
    private const string SubmittedStateId = "submitted";

    /// <summary>
    /// RA-254 fixture seed key. Referenced by name (rather than repeating the
    /// literal) by <see cref="ReAccreditationExporterFixtureBackfillMigration"/>
    /// so a future rename of the key fails to compile there instead of
    /// silently making <c>GetByIdAsync</c> return null for the fixture.
    /// </summary>
    public const string FullPayloadVerificationSeedKey = "full-payload-verification";

    /// <summary>
    /// RA-292 fixture seed key. Its own key rather than an enrichment of
    /// <c>full-payload-verification</c> on purpose:
    /// <see cref="IWorkItemPersistence.CreateIfAbsentAsync"/> inserts by
    /// deterministic id and never updates, so enriching an existing seed item
    /// would be invisible in every environment that has already seeded (dev,
    /// and any e2e stack with a persistent volume). A new key is inserted on
    /// the next boot regardless of what is already there.
    /// </summary>
    public const string OrsInterimAuthoritySeedKey = "ors-interim-authority";

    /// <summary>
    /// Organisation name of the RA-292 fixture. Unique across the seed set and
    /// not created by any spec, so an mgmt-tests search by organisation name
    /// resolves to exactly one row.
    /// </summary>
    public const string OrsInterimAuthorityOrganisationName =
        "Overseas Reprocessing Verification Ltd";

    /// <summary>
    /// RA-412 fixture seed key. Its own key for the same reason as
    /// <see cref="OrsInterimAuthoritySeedKey"/> — a new key is inserted on the
    /// next boot regardless of what an already-seeded database has.
    /// </summary>
    public const string GlobalGlassExportsSeedKey = "global-glass-exports";

    /// <summary>
    /// Organisation name of the RA-412 fixture — org 50006 in the ticket's own
    /// example. Unique across the seed set so an mgmt-tests search by
    /// organisation name resolves to exactly one row, the same discipline as
    /// <see cref="OrsInterimAuthorityOrganisationName"/>.
    /// </summary>
    public const string GlobalGlassExportsOrganisationName = "Global Glass Exports";

    /// <summary>
    /// RA-434 fixture seed key. The "Additional information" tab's Site
    /// address row falls back to the registered address for exporters
    /// (re-ex has no site for an exporter), and that fallback has no
    /// fixture to exercise it without a payload that is genuinely
    /// <c>wasteProcessingType: "exporter"</c> and carries no
    /// <c>siteAddress</c> at all. A new key rather than enriching an
    /// existing seed for the same reason as <see cref="OrsInterimAuthoritySeedKey"/>.
    /// </summary>
    public const string AdditionalInformationExporterSeedKey = "additional-information-exporter";

    /// <summary>
    /// Organisation name of the RA-434 exporter fixture. Unique across the
    /// seed set for the same reason as <see cref="OrsInterimAuthorityOrganisationName"/>.
    /// </summary>
    public const string AdditionalInformationExporterOrganisationName =
        "Continental Exports Verification Ltd";

    /// <summary>
    /// RA-434-processortype fixture seed key. The Additional information
    /// tab's "absent wasteProcessingType defaults to reprocessor" branch was
    /// originally covered by reusing <see cref="AdditionalInformationExporterSeedKey"/>'s
    /// sibling, <c>full-payload-verification</c> — which carried no
    /// <c>wasteProcessingType</c> at the time. RA-434-processortype gave
    /// <c>full-payload-verification</c> an explicit <c>wasteProcessingType:
    /// "exporter"</c> (its BES/ORS fixture requires it, now that the frontend
    /// gates those sections on the real field rather than on
    /// <c>overseasSites</c> presence), which leaves no seed item without the
    /// field. This key exists purely to keep that branch covered.
    /// </summary>
    public const string AdditionalInformationReprocessorSeedKey =
        "additional-information-reprocessor";

    /// <summary>
    /// Organisation name of the RA-434-processortype reprocessor fixture.
    /// Unique across the seed set for the same reason as
    /// <see cref="OrsInterimAuthorityOrganisationName"/>.
    /// </summary>
    public const string AdditionalInformationReprocessorOrganisationName =
        "Thames Reprocessing Verification Ltd";

    public string TypeId => ReAccreditationType.Id;

    public IEnumerable<WorkItem> Build(IWorkItemType type, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(time);

        var now = time.GetUtcNow().UtcDateTime;

        // RA-316: every payload below carries chargeAmountPence (integer pence,
        // matching the operator backend's fee bands: £546 / £2,184 / £3,276 /
        // £3,965, plus £328 per overseas reprocessing site). The operator
        // backend supplies it on real submissions, but seeded items are what
        // every non-production environment — including the journey-test stack —
        // actually renders, and the duly-making page shows this value as the
        // charge the regulator confirms before recording payment. Without it
        // that page renders "Not provided" on the one screen whose entire
        // purpose is confirming a payment, and the e2e assertions that the
        // charge is real money would fail against a backend that is working
        // correctly. Same reasoning as the RA-295 SLA-clock stamp further down:
        // seed the field the real pipeline supplies, so the UI is exercised
        // outside production.
        //
        // paymentReference is deliberately set on ONE seed only
        // (full-payload-verification). It is an override, absent on real
        // submissions because the operator backend has no payment reference at
        // submission time, and the frontend falls back to the application
        // reference. Seeding it everywhere would leave that fallback — the path
        // almost every real work item takes — completely unexercised.

        // The eight simple, single-field-varying items below are data-driven
        // (s_simpleFixtures + one Build/SimpleSeedPayload call) rather than
        // eight near-identical yield-return blocks: once RA-448 phase 2 added
        // an operatorOrganisationId argument to each, the repeated multi-line
        // call shape re-tripped SonarCloud's duplicate-code gate on new code
        // the same way the payload literals themselves did before
        // SimpleSeedPayload existed (see its doc comment) — same fix, one
        // level up.
        foreach (var spec in s_simpleFixtures)
        {
            yield return Build(
                seedKey: spec.SeedKey,
                postcode: spec.Postcode,
                submittedDaysAgo: spec.SubmittedDaysAgo,
                stateId: spec.StateId,
                payload: SimpleSeedPayload(
                    spec.OrganisationName,
                    spec.RegistrationNumber,
                    spec.OperatorApplicationId,
                    spec.OperatorRegistrationId,
                    spec.OperatorOrganisationId,
                    spec.Material,
                    spec.PreviousAccreditationYear,
                    spec.ComplianceIssuesReported,
                    spec.OperatorEmail,
                    spec.CompaniesHouseNumber,
                    spec.SiteAddress,
                    spec.SiteAddressPostcode,
                    spec.ChargeAmountPence,
                    spec.GlassRecyclingProcess
                ),
                submittedBy: StubPortalClientId,
                assignedToId: spec.AssignedToId,
                assignedToName: spec.AssignedToName,
                now: now
            );
        }

        // RA-254: carries every field a real operator submission can send —
        // including submittedBy, prns, businessPlan and samplingPlan, which
        // none of the items above populate. Used by the mgmt-tests e2e suite
        // to verify the Application details page renders the full payload
        // rather than just the subset the other seed items happen to cover.
        var fullPayloadVerificationItem = Build(
            seedKey: FullPayloadVerificationSeedKey,
            postcode: "EC1A 1BB",
            submittedDaysAgo: 4,
            stateId: SubmittedStateId,
            payload: new BsonDocument
            {
                [OrganisationNameKey] = "Full Payload Verification Ltd",
                [RegistrationNumberKey] = "EPR-100999",
                [OperatorApplicationIdKey] = "app-full-payload-001",
                // RA-412: this item carries overseasSites/BES evidence below, so
                // it must declare the real discriminator management-fe's
                // isExporterApplication() now reads — without it the item reads
                // as a Reprocessor and the BES/ORS sections stop rendering.
                // RA-434-processortype independently relies on the same field
                // for the same reason (its BES/ORS fixture).
                [WasteProcessingTypeKey] = ExporterProcessingType,
                // RA-412 (self-review): AccreditationIdGenerator and
                // ApplicationReferenceGenerator both require this on the
                // Exporter branch (the registered-office postcode, per
                // RA-314 AC01/AC02 — an Exporter's regulator is resolved from
                // here, not the site address). Deliberately a different
                // postcode/nation from siteAddressPostcode below so approving
                // this fixture actually exercises that distinction instead of
                // accidentally passing either way.
                [CompanyRegisterAddressPostcodeKey] = "G2 1AL",
                [MaterialKey] = PlasticMaterial,
                [AccreditationYearKey] = 2026,
                [PreviousAccreditationYearKey] = 2025,
                [ComplianceIssuesReportedKey] = 0,
                [OperatorEmailKey] = "full.payload@example.com",
                // RA-434: distinct from siteAddress on purpose, so a template
                // that accidentally aliases the two fields is caught by any
                // assertion comparing them.
                [CompaniesHouseNumberKey] = "12345678",
                ["companyRegisteredAddress"] = "100 Registered Office Road, London, EC1A 1AB",
                [SiteAddressKey] = "1 Full Payload Lane, London",
                [SiteAddressPostcodeKey] = "EC1A 1BB",
                ["permitNumbers"] = new BsonArray { "WML999000", "PPC888777" },
                [ChargeAmountPenceKey] = 327600,
                ["paymentReference"] = "PAY-FULL-PAYLOAD-001",
                ["submittedBy"] = new BsonDocument
                {
                    [FullNameKey] = "Priya Sharma",
                    ["jobTitle"] = "Compliance Manager",
                    [EmailKey] = "priya.sharma@example.com",
                },
                ["prns"] = new BsonDocument
                {
                    ["plannedTonnageBand"] = "UpTo5000",
                    ["authorisers"] = new BsonArray
                    {
                        new BsonDocument
                        {
                            [FullNameKey] = "Tom Baker",
                            [EmailKey] = "tom.baker@example.com",
                        },
                    },
                },
                ["businessPlan"] = new BsonDocument
                {
                    ["newInfrastructurePercent"] = 20,
                    ["priceSupportPercent"] = 15,
                    ["businessCollectionsPercent"] = 25,
                    ["communicationsPercent"] = 10,
                    ["newMarketsPercent"] = 20,
                    ["newUsesPercent"] = 10,
                    ["newInfrastructureDetail"] = "New sorting line investment",
                    ["priceSupportDetail"] = "Subsidised collection scheme",
                    ["businessCollectionsDetail"] = "Kerbside collection expansion",
                    ["communicationsDetail"] = "Customer awareness campaign",
                    ["newMarketsDetail"] = "New export contracts secured",
                    ["newUsesDetail"] = "Recycled content packaging trial",
                },
                ["samplingPlan"] = new BsonDocument
                {
                    [FilesKey] = new BsonArray
                    {
                        new BsonDocument
                        {
                            [FileIdKey] = "sampling-plan-001",
                            [FilenameKey] = "sampling-plan.pdf",
                            [ContentTypeKey] = PdfContentType,
                            // A real operator submission sends this field as
                            // plain JSON (System.Text.Json serialises DateTime
                            // as an ISO-8601 string), which lands in Mongo as a
                            // BSON string — not a BsonDateTime. Using a native
                            // DateTime here would round-trip through the API as
                            // `{"$date": "..."}`, which the "Uploaded at" GDS
                            // date filter can't parse (it expects a string).
                            [UploadedAtKey] = "2026-06-01T10:00:00.000Z",
                            [ScanStatusKey] = CleanScanStatus,
                            // Matches the fixture object seeded into floci's
                            // S3 bucket by the mgmt-tests compose stack
                            // (docker/scripts/localstack/10-setup-buckets.sh),
                            // so the download link this seed item exercises
                            // resolves to a real object end-to-end.
                            [S3KeyField] =
                                "sampling-plans/full-payload-verification/sampling-plan.pdf",
                            [S3BucketField] = "epr-register-enrol-sampling-plans",
                        },
                        // RA-295 / AC02: the sampling & inspection plan "could
                        // have other supporting docs and should be listed", so
                        // the fixture needs more than one file — with a single
                        // entry a "lists every document" assertion passes even
                        // against a template that renders files[0] and stops.
                        new BsonDocument
                        {
                            [FileIdKey] = "sampling-plan-002",
                            [FilenameKey] = "sampling-plan-appendix.pdf",
                            [ContentTypeKey] = PdfContentType,
                            [UploadedAtKey] = "2026-06-02T10:00:00.000Z",
                            [ScanStatusKey] = CleanScanStatus,
                            // Backed by a real object in the mgmt-tests
                            // localstack bucket (seeded by
                            // docker/scripts/localstack/10-setup-buckets.sh in
                            // that repo), so the e2e suite fetches this href
                            // and asserts a 200 + PDF content type. Keep the
                            // two s3Keys distinct: an href bug that serves
                            // file one for both documents is invisible to a
                            // filename-only assertion.
                            [S3KeyField] =
                                "sampling-plans/full-payload-verification/sampling-plan-appendix.pdf",
                            [S3BucketField] = "epr-register-enrol-sampling-plans",
                        },
                    },
                },
                // Matches the fixture object seeded into floci's S3 bucket by the
                // mgmt-tests compose stack (docker/scripts/localstack/10-setup-buckets.sh),
                // so the BES-evidence download link this seed item exercises resolves
                // to a real object end-to-end — mirrors the samplingPlan fixture above.
                ["overseasSites"] = new BsonDocument
                {
                    ["sites"] = new BsonArray
                    {
                        new BsonDocument
                        {
                            [SiteIdKey] = 1,
                            [SiteNameKey] = "Full Payload Verification Overseas Site",
                            [SiteAddressKey] = "1 Overseas Lane, Rotterdam",
                            [CountryKey] = "Netherlands",
                            [BesEvidenceKey] = new BsonDocument
                            {
                                [FilesKey] = new BsonArray
                                {
                                    new BsonDocument
                                    {
                                        [FileIdKey] = "bes-evidence-001",
                                        [FilenameKey] = "bes-evidence.pdf",
                                        [ContentTypeKey] = PdfContentType,
                                        [UploadedAtKey] = "2026-06-01T10:00:00.000Z",
                                        [ScanStatusKey] = CleanScanStatus,
                                        [S3KeyField] =
                                            "bes-evidence/full-payload-verification/bes-evidence.pdf",
                                        [S3BucketField] = "epr-register-enrol-bes-evidence",
                                    },
                                },
                            },
                        },
                    },
                },
            },
            submittedBy: StubPortalClientId,
            now: now
        );
        SetAccreditationNumberFields(
            fullPayloadVerificationItem.Payload,
            "500009",
            "reg-full-payload-001"
        );
        yield return fullPayloadVerificationItem;

        // RA-292: the ORS / interim-site / authority-to-issue fixture.
        //
        // AC01-AC03 are "is this thing flagged as NEW?" assertions, and a
        // fixture that only carries the positive case cannot tell a correct
        // implementation from one that badges everything. So every flag appears
        // here in all three of its observable forms — true, false, and absent —
        // on a single work item:
        //
        //   overseasSites.sites[0]  isNewSite = true   + interimSite isNewSite = true
        //   overseasSites.sites[1]  isNewSite = false  + interimSite isNewSite = false
        //   overseasSites.sites[2]  isNewSite absent   + no interimSite at all
        //   overseasSites.sites[3]  isNewSite = false  + no interimSite; isEu/isOecd false
        //   prns.authorisers[0]     isNew = true
        //   prns.authorisers[1]     isNew = false
        //   prns.authorisers[2]     isNew absent
        //
        // The absent variants are not padding: every RA-292 field is optional
        // on the wire, so "absent renders no badge and does not throw" is a real
        // branch of the frontend. The whole-item backwards-compatibility case
        // (no overseasSites and no prns at all, i.e. a pre-RA-292 submission) is
        // covered by the "Belfast Fibres Co" item above, which mgmt-tests
        // already uses as its no-overseas-sites fixture.
        //
        // Site 0 carries the full ORS detail field set for AC04. The rest
        // deliberately vary it so a template that assumes every key is present
        // fails here rather than in production: site 1 has an EMPTY besEvidence
        // file list and an interim site with no addressLine2, site 2 is a
        // pre-RA-292 document missing the flags entirely, site 3 is non-EU /
        // non-OECD with conditionsOfExport absent.
        //
        // Field TYPES below are taken from a captured operator-backend payload,
        // not from its model definitions, and two of them are counter-intuitive:
        // `repatriatedLoads` is a STRING and `conditionsOfExport` a nullable
        // BOOLEAN. `coordinates` is a string, not a lat/long object. Optional
        // fields are absent KEYS, never nulls — the producer serialises with
        // WhenWritingNull.
        var orsInterimAuthorityItem = Build(
            seedKey: OrsInterimAuthoritySeedKey,
            postcode: "EC2A 2BB",
            submittedDaysAgo: 6,
            stateId: SubmittedStateId,
            payload: new BsonDocument
            {
                [OrganisationNameKey] = OrsInterimAuthorityOrganisationName,
                [RegistrationNumberKey] = "EPR-100292",
                [OperatorApplicationIdKey] = "app-ors-interim-authority-001",
                // RA-412: see the same field on the full-payload-verification
                // item above — this item's overseasSites/ORS sites need it too.
                // RA-434-processortype independently relies on the same field
                // for the same reason (this is the RA-292 ORS/BES fixture).
                [WasteProcessingTypeKey] = ExporterProcessingType,
                // RA-412 (self-review): see the same field on
                // full-payload-verification above for why it's required.
                [CompanyRegisterAddressPostcodeKey] = "SA1 1AA",
                [MaterialKey] = PlasticMaterial,
                [AccreditationYearKey] = 2026,
                [PreviousAccreditationYearKey] = 2025,
                [ComplianceIssuesReportedKey] = 0,
                [OperatorEmailKey] = "ors.verification@example.com",
                [CompaniesHouseNumberKey] = "12131415",
                [SiteAddressKey] = "1 Verification Way, London",
                [SiteAddressPostcodeKey] = "EC2A 2BB",
                [ChargeAmountPenceKey] = 218400,
                ["submittedBy"] = new BsonDocument
                {
                    [FullNameKey] = "Grace Adeyemi",
                    ["jobTitle"] = "Head of Compliance",
                    [EmailKey] = "grace.adeyemi@example.com",
                },
                // AC03: authority-to-issue contacts, one of each flag state.
                ["prns"] = new BsonDocument
                {
                    ["plannedTonnageBand"] = "UpTo5000",
                    ["authorisers"] = new BsonArray
                    {
                        new BsonDocument
                        {
                            [FullNameKey] = "Grace Adeyemi",
                            [EmailKey] = "grace.adeyemi@example.com",
                            ["isNew"] = true,
                        },
                        new BsonDocument
                        {
                            [FullNameKey] = "Martin Cole",
                            [EmailKey] = "martin.cole@example.com",
                            ["isNew"] = false,
                        },
                        // No isNew key — a pre-RA-292 authoriser record.
                        new BsonDocument
                        {
                            [FullNameKey] = "Priya Nair",
                            [EmailKey] = "priya.nair@example.com",
                        },
                    },
                },
                ["overseasSites"] = new BsonDocument
                {
                    ["sites"] = new BsonArray
                    {
                        // AC01 + AC02 positive case, and the AC04 detail set.
                        new BsonDocument
                        {
                            [SiteIdKey] = 1,
                            [OrsIdKey] = "ORS-2026-0292",
                            [SiteNameKey] = "Rotterdam New Reprocessing Site",
                            [SiteAddressKey] = "1 Havenstraat, Rotterdam",
                            [AddressLine1Key] = "1 Havenstraat",
                            [AddressLine2Key] = "Europoort Industrial Park",
                            [TownOrCityKey] = "Rotterdam",
                            [CountryKey] = "Netherlands",
                            ["coordinates"] = "51.9244, 4.4777",
                            [ContactNameKey] = "Johan de Vries",
                            [ContactEmailKey] = "johan.devries@example.com",
                            [ContactPhoneKey] = "+31 10 123 4567",
                            ["operationCode"] = "R3",
                            ["code1"] = "B3011",
                            ["code2"] = "GH013",
                            ["code3"] = "Y48",
                            // Confirmed against a captured operator-backend
                            // payload (RA-292): repatriatedLoads is a STRING
                            // and conditionsOfExport a nullable BOOLEAN, not
                            // the number and free-text they read like.
                            ["repatriatedLoads"] = "3",
                            ["conditionsOfExport"] = true,
                            ["isEu"] = true,
                            ["isOecd"] = true,
                            [IsNewSiteKey] = true,
                            ["registeredNowAccredited"] = false,
                            [BesEvidenceKey] = new BsonDocument
                            {
                                [FilesKey] = new BsonArray
                                {
                                    new BsonDocument
                                    {
                                        [FileIdKey] = "ra292-bes-evidence-001",
                                        [FilenameKey] = "bes-evidence.pdf",
                                        [ContentTypeKey] = PdfContentType,
                                        [UploadedAtKey] = "2026-06-01T10:00:00.000Z",
                                        [ScanStatusKey] = CleanScanStatus,
                                        ["besEvidenceValidFromDate"] = "2026-01-01T00:00:00Z",
                                        ["besEvidenceExpiryDate"] = "2027-01-01T00:00:00Z",
                                        // Deliberately the same S3 object the
                                        // full-payload fixture uses: it is
                                        // already seeded into the mgmt-tests
                                        // localstack bucket, so this item's
                                        // download link resolves end-to-end
                                        // without a new fixture object. The
                                        // fileId is distinct because
                                        // download-file.controller.js resolves
                                        // files by fileId within one work item.
                                        [S3KeyField] =
                                            "bes-evidence/full-payload-verification/bes-evidence.pdf",
                                        [S3BucketField] = "epr-register-enrol-bes-evidence",
                                    },
                                },
                            },
                            ["interimSite"] = new BsonDocument
                            {
                                [SiteIdKey] = 11,
                                ["siteNumber"] = "INT-001",
                                [IsNewSiteKey] = true,
                                [CountryKey] = "Belgium",
                                [SiteNameKey] = "Antwerp Interim Holding Site",
                                [AddressLine1Key] = "12 Scheldelaan",
                                [AddressLine2Key] = "Unit 4",
                                [TownOrCityKey] = "Antwerp",
                                ["stateOrRegion"] = "Flanders",
                                ["postcode"] = "2030",
                                [ContactNameKey] = "Elke Janssens",
                                [ContactEmailKey] = "elke.janssens@example.com",
                                [ContactPhoneKey] = "+32 3 987 6543",
                            },
                        },
                        // AC01 + AC02 negative case: an established site and an
                        // established interim site. Empty besEvidence file
                        // list, and the interim site has no addressLine2.
                        new BsonDocument
                        {
                            [SiteIdKey] = 2,
                            [OrsIdKey] = "ORS-2024-0042",
                            [SiteNameKey] = "Hamburg Established Reprocessing Site",
                            [SiteAddressKey] = "42 Hafenstrasse, Hamburg",
                            [AddressLine1Key] = "42 Hafenstrasse",
                            [AddressLine2Key] = "Building C",
                            [TownOrCityKey] = "Hamburg",
                            [CountryKey] = "Germany",
                            ["coordinates"] = "53.5511, 9.9937",
                            [ContactNameKey] = "Anna Schmidt",
                            [ContactEmailKey] = "anna.schmidt@example.com",
                            [ContactPhoneKey] = "+49 40 555 0142",
                            ["operationCode"] = "R4",
                            ["code1"] = "B1010",
                            ["code2"] = "GA300",
                            ["code3"] = "Y23",
                            // Falsy-but-present: must render as "0" and "No",
                            // not be swallowed by a truthiness check.
                            ["repatriatedLoads"] = "0",
                            ["conditionsOfExport"] = false,
                            ["isEu"] = true,
                            ["isOecd"] = true,
                            [IsNewSiteKey] = false,
                            ["registeredNowAccredited"] = true,
                            // The producer always emits besEvidence with a
                            // files array; a site with no evidence yet sends an
                            // EMPTY array rather than omitting the key. That is
                            // its own rendering branch, distinct from both a
                            // populated list and an absent key.
                            [BesEvidenceKey] = new BsonDocument { [FilesKey] = new BsonArray() },
                            ["interimSite"] = new BsonDocument
                            {
                                [SiteIdKey] = 21,
                                ["siteNumber"] = "INT-002",
                                [IsNewSiteKey] = false,
                                [CountryKey] = "Germany",
                                [SiteNameKey] = "Bremen Interim Storage",
                                [AddressLine1Key] = "8 Speicherstrasse",
                                [TownOrCityKey] = "Bremen",
                                ["stateOrRegion"] = "Bremen",
                                ["postcode"] = "28217",
                                [ContactNameKey] = "Lukas Braun",
                                [ContactEmailKey] = "lukas.braun@example.com",
                                [ContactPhoneKey] = "+49 421 555 0188",
                            },
                        },
                        // Pre-RA-292 shape: no isNewSite, no interimSite, no
                        // besEvidence. Proves absent flags render as "not new"
                        // rather than crashing or badging.
                        //
                        // The current producer never emits a site like this —
                        // isNewSite/isEu/isOecd/registeredNowAccredited are
                        // non-nullable there and besEvidence is always present.
                        // This models a document PERSISTED BEFORE RA-292, which
                        // is exactly the shape already sitting in the database
                        // and the one the frontend must not choke on. It is
                        // deliberately not a facsimile of a live submission.
                        //
                        // Optional fields are absent KEYS, never null values —
                        // the producer serialises with WhenWritingNull, so a
                        // null-valued key would misrepresent a real payload.
                        new BsonDocument
                        {
                            [SiteIdKey] = 3,
                            [OrsIdKey] = "ORS-2023-0007",
                            [SiteNameKey] = "Bilbao Legacy Reprocessing Site",
                            [SiteAddressKey] = "7 Muelle Tomas Olabarri, Bilbao",
                            [TownOrCityKey] = "Bilbao",
                            [CountryKey] = "Spain",
                        },
                        // Non-EU, non-OECD site. Without this the fixture had
                        // no `isEu: false` / `isOecd: false` anywhere, so a
                        // frontend that renders those two field-by-field rather
                        // than through a shared boolean helper could drop the
                        // false case unnoticed. Malaysia is genuinely neither
                        // EU nor OECD — the branch is reached with a factually
                        // honest fixture rather than by mislabelling Germany.
                        //
                        // Also the one otherwise-complete site with
                        // conditionsOfExport absent: that field is nullable at
                        // the producer, unlike the other flags, so "absent on a
                        // complete site" is a legitimate shape.
                        new BsonDocument
                        {
                            [SiteIdKey] = 4,
                            [OrsIdKey] = "ORS-2025-0113",
                            [SiteNameKey] = "Port Klang Reprocessing Facility",
                            [SiteAddressKey] = "88 Jalan Pelabuhan, Port Klang",
                            [AddressLine1Key] = "88 Jalan Pelabuhan",
                            [AddressLine2Key] = "Zone 3",
                            [TownOrCityKey] = "Port Klang",
                            [CountryKey] = "Malaysia",
                            ["coordinates"] = "3.0044, 101.3928",
                            [ContactNameKey] = "Aisyah Rahman",
                            [ContactEmailKey] = "aisyah.rahman@example.com",
                            [ContactPhoneKey] = "+60 3 3168 8000",
                            ["operationCode"] = "R3",
                            ["code1"] = "B3011",
                            ["code2"] = "GH013",
                            ["code3"] = "Y48",
                            ["repatriatedLoads"] = "2",
                            ["isEu"] = false,
                            ["isOecd"] = false,
                            [IsNewSiteKey] = false,
                            ["registeredNowAccredited"] = false,
                            [BesEvidenceKey] = new BsonDocument { [FilesKey] = new BsonArray() },
                        },
                    },
                },
            },
            submittedBy: StubPortalClientId,
            now: now
        );
        SetAccreditationNumberFields(
            orsInterimAuthorityItem.Payload,
            "500010",
            "reg-ors-interim-authority-001"
        );
        yield return orsInterimAuthorityItem;

        // RA-412: a genuine Exporter organisation — org 50006 "Global Glass
        // Exports" in the ticket's own example. Unlike
        // full-payload-verification/ors-interim-authority above (which only
        // need wasteProcessingType so their overseasSites data reads
        // correctly), this item's whole point IS being a real Exporter
        // application: it proves the work-items card label and the
        // Applicant-type filter both resolve a genuine exporter, not just
        // avoid mislabelling one that happens to carry overseas site data.
        // Deliberately a plain item with no overseasSites/BES payload of its
        // own — that positive case is already covered by the two fixtures
        // above.
        var globalGlassExportsItem = Build(
            seedKey: GlobalGlassExportsSeedKey,
            postcode: "M1 1AE",
            submittedDaysAgo: 7,
            stateId: SubmittedStateId,
            payload: new BsonDocument
            {
                [OrganisationNameKey] = GlobalGlassExportsOrganisationName,
                [RegistrationNumberKey] = "EPR-100506",
                [OperatorApplicationIdKey] = "app-global-glass-exports-001",
                [WasteProcessingTypeKey] = ExporterProcessingType,
                // RA-412 (self-review): see the same field on
                // full-payload-verification above for why it's required.
                [CompanyRegisterAddressPostcodeKey] = "CF10 1AA",
                [MaterialKey] = "glass",
                [PreviousAccreditationYearKey] = 2025,
                [ComplianceIssuesReportedKey] = 0,
                [OperatorEmailKey] = "global.glass.exports@example.com",
                [CompaniesHouseNumberKey] = "11121314",
                [SiteAddressPostcodeKey] = "M1 1AE",
                [ChargeAmountPenceKey] = 54600,
            },
            submittedBy: StubPortalClientId,
            now: now
        );
        SetAccreditationNumberFields(globalGlassExportsItem.Payload, "500011", "reg-050006");
        yield return globalGlassExportsItem;

        // RA-434: an Exporter-type item, carrying the three fields new to the
        // "Additional information" tab (companiesHouseNumber,
        // companyRegisteredAddress, permitNumbers) and — the point of this
        // fixture — NO siteAddress at all. Re-ex has no site for an exporter,
        // so the CM frontend falls back to companyRegisteredAddress for the
        // Site address row; every other seed item is reprocessor-shaped
        // (siteAddress present) and cannot exercise that branch.
        //
        // companyRegisterAddressPostcode (note: no 'd' — the existing,
        // postcode-only key AccreditationIdGenerator / ApplicationReferenceGenerator
        // already read for an exporter's regulator postcode) is set alongside
        // the new full-address companyRegisteredAddress key so this fixture is
        // a realistic exporter payload, not just enough to pass the new tab's
        // tests.
        var additionalInformationExporterItem = Build(
            seedKey: AdditionalInformationExporterSeedKey,
            postcode: "CT16 1AA",
            submittedDaysAgo: 4,
            stateId: SubmittedStateId,
            payload: new BsonDocument
            {
                [OrganisationNameKey] = AdditionalInformationExporterOrganisationName,
                [RegistrationNumberKey] = "EPR-100434",
                [OperatorApplicationIdKey] = "app-additional-info-exporter-001",
                [MaterialKey] = PlasticMaterial,
                [AccreditationYearKey] = 2026,
                [PreviousAccreditationYearKey] = 2025,
                [ComplianceIssuesReportedKey] = 0,
                [OperatorEmailKey] = "continental.exports@example.com",
                [WasteProcessingTypeKey] = ExporterProcessingType,
                [CompaniesHouseNumberKey] = "09876543",
                ["companyRegisteredAddress"] = "1 Continental Way, Dover, Kent",
                [CompanyRegisterAddressPostcodeKey] = "CT16 1AA",
                ["permitNumbers"] = new BsonArray { "WML123456", "PPC456789" },
                [ChargeAmountPenceKey] = 218400,
            },
            submittedBy: StubPortalClientId,
            now: now
        );
        SetAccreditationNumberFields(
            additionalInformationExporterItem.Payload,
            "500012",
            "reg-additional-info-exporter-001"
        );
        yield return additionalInformationExporterItem;

        // RA-434-processortype: the reprocessor counterpart to
        // AdditionalInformationExporterSeedKey above. Genuinely carries NO
        // wasteProcessingType key at all — that absence is the point, so the
        // Additional information tab's Site address row exercises the
        // "defaults to reprocessor" branch. Also gives the tab's other RA-434
        // fields (companiesHouseNumber, companyRegisteredAddress,
        // permitNumbers) a fixture that stays reprocessor-shaped even after
        // full-payload-verification became an explicit exporter for its own
        // BES/ORS fixture.
        var additionalInformationReprocessorItem = Build(
            seedKey: AdditionalInformationReprocessorSeedKey,
            postcode: "SE1 9GF",
            submittedDaysAgo: 4,
            stateId: SubmittedStateId,
            payload: new BsonDocument
            {
                [OrganisationNameKey] = AdditionalInformationReprocessorOrganisationName,
                [RegistrationNumberKey] = "EPR-100435",
                [OperatorApplicationIdKey] = "app-additional-info-reprocessor-001",
                [MaterialKey] = PlasticMaterial,
                [AccreditationYearKey] = 2026,
                [PreviousAccreditationYearKey] = 2025,
                [ComplianceIssuesReportedKey] = 0,
                [OperatorEmailKey] = "thames.reprocessing@example.com",
                [CompaniesHouseNumberKey] = "13579246",
                // Deliberately DIFFERENT from siteAddress below — a template
                // that accidentally aliases the two fields would otherwise
                // pass unnoticed (same reasoning as the exporter fixture).
                ["companyRegisteredAddress"] = "200 Registered Office Road, London, SE1 9AA",
                [SiteAddressKey] = "1 Thames Reprocessing Way, London",
                [SiteAddressPostcodeKey] = "SE1 9GF",
                ["permitNumbers"] = new BsonArray { "WML135792", "PPC468024" },
                [ChargeAmountPenceKey] = 218400,
            },
            submittedBy: StubPortalClientId,
            now: now
        );
        SetAccreditationNumberFields(
            additionalInformationReprocessorItem.Payload,
            "500013",
            "reg-additional-info-reprocessor-001"
        );
        yield return additionalInformationReprocessorItem;
    }

    /// <summary>
    /// Build a single seeded work item with a realistic audit trail.
    ///
    /// RA-175: the method is no longer <c>static</c> so it can access
    /// the injected <see cref="INationResolver"/> to derive
    /// <c>payload.nation</c> from the postcode using the same logic that
    /// <see cref="ReAccreditationNationRoutingHook"/> applies to real
    /// submissions. The audit log mirrors what
    /// <see cref="WorkItemService"/> and the hooks write, so the seeded
    /// items have a plausible processing history.
    /// </summary>
    private WorkItem Build(
        string seedKey,
        string postcode,
        int submittedDaysAgo,
        string stateId,
        BsonDocument payload,
        string submittedBy,
        DateTime now,
        string? assignedToId = null,
        string? assignedToName = null
    )
    {
        var submittedAt = now.AddDays(-submittedDaysAgo);
        var assignedAt = assignedToId is null ? (DateTime?)null : submittedAt.AddHours(2);
        var lastModifiedAt = assignedAt ?? submittedAt;

        // RA-175: derive nation from postcode using the same resolver as
        // ReAccreditationNationRoutingHook so the camelCase payload.nation
        // key matches the MongoDB index and the nation filter in the work
        // queue correctly surfaces this item.
        var nation = nationResolver.Resolve(postcode);
        payload["nation"] = nation.ToString();

        // RA-196: ensure applicationReference is in the payload.
        var applicationReference = GenerateDeterministicReference(seedKey);
        payload["applicationReference"] = applicationReference;

        var workItem = new WorkItem
        {
            // Deterministic id keyed by (TypeId, seedKey) so re-running
            // the seeder is idempotent and concurrent instances cannot
            // create duplicates (epr-33c).
            Id = WorkItemSeed.DeterministicId(ReAccreditationType.Id, seedKey),
            TypeId = ReAccreditationType.Id,
            StateId = stateId,
            SubmittedAt = submittedAt,
            LastModifiedAt = lastModifiedAt,
            SubmittedBy = submittedBy,
            AssignedToId = assignedToId,
            AssignedToName = assignedToName,
            AssignedAt = assignedAt,
            // epr-ce4: seeded assignments are attributed to a sentinel
            // ("system:seeder"), never to the assignee id — that would
            // falsify the audit trail to claim the user assigned
            // themselves.
            AssignedBy = assignedToId is null ? null : SeederAssignedBy,
            // RA-295: every state after `submitted` is only reachable via
            // `duly-made`, and that transition stamps the SLA clock
            // (ReAccreditationDulyMadeHook). Seeded items skip `duly-made`
            // entirely, so they used to carry a null clock — which meant the
            // Applications card and the individual case header rendered no
            // "Due on" date anywhere outside production. Stamp the clock the
            // way the hook would have: the day after submission. Items still
            // in `submitted` correctly keep a null clock (and therefore a null
            // slaDueDate), so both the populated and the empty case are
            // represented in the seed set.
            //
            // Beware when writing assertions against a `submitted` fixture:
            // slaDueDate, slaRemaining and slaState are all null *together*, so
            // any UI rendered behind a truthiness check on one of them does not
            // exist on such an item at all. A test asserting that some
            // SLA-derived element is absent therefore passes against a build
            // that still renders it. Point those at an item with a clock (or
            // pin one via the SLA override endpoint) — this already made an
            // mgmt-tests SLA-badge-removal spec silently vacuous.
            SlaClock =
                stateId == SubmittedStateId
                    ? null
                    : new WorkItemSlaClock { StartedAt = submittedAt.AddDays(1) },
            Payload = payload,
        };

        // RA-175: seed a realistic audit trail so the timeline view has
        // plausible history.  Mirrors what WorkItemService.SubmitAsync and
        // the post-submission hooks append for real items.

        // Birth event (mirrors WorkItemService.SubmitAsync).
        workItem.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = "work-item-submitted",
                ActionDisplayName = "Work item submitted",
                Details = new Dictionary<string, string?>
                {
                    ["typeId"] = ReAccreditationType.Id,
                    // The state AT SUBMISSION, which is always the type's
                    // initial state -- NOT the seed's terminal `stateId`.
                    // This feeds the entry's own "Initial state" row.
                    ["stateId"] = SubmittedStateId,
                    ["source"] = "seeder",
                    ["clientId"] = submittedBy,
                    ["applicationReference"] = applicationReference,
                },
                CreatedAt = submittedAt,
                CreatedBy = submittedBy,
                CreatedByName = null,
                // epr-rr9s: mirror the real submit path — the birth entry
                // carries the initial state. NOT the seed's terminal
                // `stateId`: stamping that would put the current state on a
                // historical entry, which is the exact bug epr-rr9s fixes.
                StateId = SubmittedStateId,
            }
        );

        // Nation routing event (mirrors ReAccreditationNationRoutingHook).
        workItem.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = "routed-to-nation",
                ActionDisplayName = "Routed to nation",
                Details = new Dictionary<string, string?>
                {
                    ["nation"] = nation.ToString(),
                    ["derivedFrom"] = "site-address",
                },
                CreatedAt = submittedAt.AddSeconds(1),
                CreatedBy = null,
                CreatedByName = null,
                // epr-rr9s: mirror ReAccreditationNationRoutingHook — routing
                // happens immediately after submission, so it snapshots the
                // initial state, not the seed's terminal one.
                StateId = SubmittedStateId,
            }
        );

        // Assignment event for assigned items (mirrors WorkItemService.AssignAsync).
        if (assignedToId is not null && assignedAt is not null)
        {
            workItem.AuditLog.Add(
                new WorkItemAuditEntry
                {
                    Action = "assigned",
                    ActionDisplayName = "Assigned",
                    Details = new Dictionary<string, string?>
                    {
                        ["assigneeId"] = assignedToId,
                        ["assigneeName"] = assignedToName,
                        ["previousAssigneeId"] = null,
                        ["previousAssigneeName"] = null,
                    },
                    CreatedAt = assignedAt.Value,
                    CreatedBy = SeederAssignedBy,
                    CreatedByName = null,
                    // epr-rr9s: mirror WorkItemService.AssignAsync — the
                    // assignment entry snapshots the item's state at the time.
                    // The seeded timeline puts assignment two hours after
                    // submission and models no transition before it, so that
                    // state is the initial one rather than the terminal
                    // `stateId`.
                    StateId = SubmittedStateId,
                }
            );
        }

        return workItem;
    }

    /// <summary>
    /// One of the eight simple, single-field-varying seed items (Acme
    /// Recycling through Belfast Fibres), fed to
    /// <see cref="SimpleSeedPayload"/> and the private <c>Build</c> helper
    /// by the <see cref="s_simpleFixtures"/> loop.
    /// </summary>
    private sealed record SimpleFixtureSpec(
        string SeedKey,
        string Postcode,
        int SubmittedDaysAgo,
        string StateId,
        string OrganisationName,
        string RegistrationNumber,
        string OperatorApplicationId,
        string OperatorRegistrationId,
        string OperatorOrganisationId,
        string Material,
        int PreviousAccreditationYear,
        int ComplianceIssuesReported,
        string OperatorEmail,
        string CompaniesHouseNumber,
        string SiteAddress,
        string SiteAddressPostcode,
        int ChargeAmountPence,
        string? GlassRecyclingProcess = null,
        string? AssignedToId = null,
        string? AssignedToName = null
    );

    private static readonly SimpleFixtureSpec[] s_simpleFixtures =
    [
        // Newly submitted, no one has picked it up yet.
        new(
            "acme-recycling",
            "SW1A 1AA",
            1,
            SubmittedStateId,
            "Acme Recycling Ltd",
            "EPR-100023",
            "app-acme-recycling-001",
            "reg-001",
            "500001",
            PlasticMaterial,
            2025,
            0,
            "acme.recycling@example.com",
            "02345678",
            "1 Acme Way, London",
            "SW1A 1AA",
            54600
        ),
        // Submitted and self-claimed by a standard user; first state still
        // has work to do.
        new(
            "northern-plastics",
            "EH1 3BN",
            3,
            SubmittedStateId,
            "Northern Plastics Co-op",
            "EPR-100087",
            "app-northern-plastics-001",
            "reg-002",
            "500002",
            PlasticMaterial,
            2025,
            1,
            "northern.plastics@example.com",
            "03456789",
            "1 Northern Plastics Court, Edinburgh",
            "EH1 3BN",
            218400,
            AssignedToId: "stub-standard-1",
            AssignedToName: "Stub Standard User"
        ),
        // Mid-assessment: assigned and under active review.
        new(
            "riverside-glass",
            "CF10 1AA",
            9,
            "assessment-in-progress",
            "Riverside Glass Recovery",
            "EPR-099812",
            "app-riverside-glass-001",
            "reg-003",
            "500003",
            "glass",
            2024,
            2,
            "riverside.glass@example.com",
            "04567890",
            "1 Riverside Way, Cardiff",
            "CF10 1AA",
            327600,
            GlassRecyclingProcess: "glass_re_melt",
            AssignedToId: StubAssigneeId,
            AssignedToName: StubAssigneeName
        ),
        // Awaiting decision: parked in the intermediate state a pre-RA-410
        // two-step decision left items in, so the single-call /decision
        // endpoint has a fixture proving it recovers them.
        new(
            "coastal-materials",
            "BT1 1AA",
            15,
            "awaiting-decision",
            "Coastal Materials Group",
            "EPR-098774",
            "app-coastal-materials-001",
            "reg-004",
            "500004",
            PlasticMaterial,
            2024,
            0,
            "coastal.materials@example.com",
            "05678901",
            "1 Coastal Materials Quay, Belfast",
            "BT1 1AA",
            396500,
            AssignedToId: StubAssigneeId,
            AssignedToName: StubAssigneeName
        ),
        // Already approved — terminal state, useful for exercising the
        // "no further actions" rendering path.
        new(
            "heritage-paper",
            "BS1 4DJ",
            32,
            "approved",
            "Heritage Paper Mills",
            "EPR-097215",
            "app-heritage-paper-001",
            "reg-005",
            "500005",
            "paper",
            2024,
            0,
            "heritage.paper@example.com",
            "06789012",
            "1 Heritage Paper Mill Road, Bristol",
            "BS1 4DJ",
            360400,
            AssignedToId: StubAssigneeId,
            AssignedToName: StubAssigneeName
        ),
        // Additional Scotland item — submitted, unassigned.
        new(
            "clyde-composites",
            "G1 1AA",
            5,
            SubmittedStateId,
            "Clyde Composites Ltd",
            "EPR-100134",
            "app-clyde-composites-001",
            "reg-006",
            "500006",
            PlasticMaterial,
            2025,
            0,
            "clyde.composites@example.com",
            "07890123",
            "1 Clyde Composites Way, Glasgow",
            "G1 1AA",
            54600
        ),
        // Additional Wales item — assessment in progress.
        new(
            "swansea-textiles",
            "SA1 1AA",
            11,
            "assessment-in-progress",
            "Swansea Textiles Recovery",
            "EPR-099441",
            "app-swansea-textiles-001",
            "reg-007",
            "500007",
            "glass",
            2024,
            1,
            "swansea.textiles@example.com",
            "08901234",
            "1 Swansea Textiles Court, Swansea",
            "SA1 1AA",
            218400,
            GlassRecyclingProcess: "glass_other",
            AssignedToId: StubAssigneeId,
            AssignedToName: StubAssigneeName
        ),
        // Additional Northern Ireland item — submitted, unassigned.
        new(
            "belfast-fibres",
            "BT7 1AA",
            2,
            SubmittedStateId,
            "Belfast Fibres Co",
            "EPR-100198",
            "app-belfast-fibres-001",
            "reg-008",
            "500008",
            "paper",
            2025,
            0,
            "belfast.fibres@example.com",
            "10111213",
            "1 Belfast Fibres Way, Belfast",
            "BT7 1AA",
            396500
        ),
    ];

    /// <summary>
    /// Builds the payload for one of <see cref="s_simpleFixtures"/>.
    /// <paramref name="glassRecyclingProcess"/> is null except for the two
    /// glass items (RA-307).
    /// </summary>
    private static BsonDocument SimpleSeedPayload(
        string organisationName,
        string registrationNumber,
        string operatorApplicationId,
        string operatorRegistrationId,
        string operatorOrganisationId,
        string material,
        int previousAccreditationYear,
        int complianceIssuesReported,
        string operatorEmail,
        string companiesHouseNumber,
        string siteAddress,
        string siteAddressPostcode,
        int chargeAmountPence,
        string? glassRecyclingProcess = null
    )
    {
        var payload = new BsonDocument
        {
            ["organisationName"] = organisationName,
            ["registrationNumber"] = registrationNumber,
            // RA-448 phase 2 review: the backend's own AccreditationApplicationModel
            // id (confirmed against HttpCaseWorkingApiAdapter.BuildPayload) — the
            // adapter's {applicationId} route segment. Seed fixtures need a
            // realistic value too so they can be approved end-to-end.
            ["operatorApplicationId"] = operatorApplicationId,
            ["operatorRegistrationId"] = operatorRegistrationId,
            // RA-448 phase 2: real submissions always carry a numeric Org ID
            // (IAccreditationNumberAdapter parses it as int); seed fixtures
            // need a realistic value too so they can be approved end-to-end.
            ["operatorOrganisationId"] = operatorOrganisationId,
            ["material"] = material,
        };
        if (glassRecyclingProcess is not null)
        {
            // RA-307: e2e coverage for the "Glass - Remelt" / "Glass - Other"
            // display suffix (see mgmt-tests glass-recycling-process.e2e.js).
            payload["glassRecyclingProcess"] = glassRecyclingProcess;
        }
        payload["previousAccreditationYear"] = previousAccreditationYear;
        payload["complianceIssuesReported"] = complianceIssuesReported;
        payload["operatorEmail"] = operatorEmail;
        payload["companiesHouseNumber"] = companiesHouseNumber;
        payload["siteAddress"] = siteAddress;
        payload["siteAddressPostcode"] = siteAddressPostcode;
        payload["chargeAmountPence"] = chargeAmountPence;
        return payload;
    }

    /// <summary>
    /// RA-448 phase 2: stamps the numeric Org ID and registration id every
    /// accreditation-number request needs onto one of the five special-case
    /// fixtures below (FullPayloadVerification, OrsInterimAuthority,
    /// GlobalGlassExports, AdditionalInformationExporter,
    /// AdditionalInformationReprocessor). Each of those five is a bespoke
    /// <c>BsonDocument</c> literal too irregular in shape to share
    /// <see cref="SimpleSeedPayload"/>, but the repeated two-line
    /// operatorOrganisationId/operatorRegistrationId shape across all five
    /// tripped SonarCloud's duplicate-code gate the same way the eight simple
    /// items did before <see cref="SimpleSeedPayload"/> existed — same fix,
    /// applied here. Mutates the already-built item's payload in place
    /// (BsonDocument is a reference type) rather than the pre-Build literal,
    /// so the surrounding fixture bodies below stay untouched instead of
    /// being reindented.
    /// </summary>
    private static void SetAccreditationNumberFields(
        BsonDocument payload,
        string operatorOrganisationId,
        string operatorRegistrationId
    )
    {
        payload["operatorOrganisationId"] = operatorOrganisationId;
        payload["operatorRegistrationId"] = operatorRegistrationId;
    }

    private static string GenerateDeterministicReference(string seedKey)
    {
        var input = System.Text.Encoding.UTF8.GetBytes(seedKey);
        var hash = System.Security.Cryptography.SHA1.HashData(input);

        // Simple stable uint from first 4 bytes
        uint val =
            ((uint)hash[0] << 24) | ((uint)hash[1] << 16) | ((uint)hash[2] << 8) | (uint)hash[3];
        var digits = 100_000_000 + (val % 900_000_000);
        return $"RA-{digits}";
    }
}
