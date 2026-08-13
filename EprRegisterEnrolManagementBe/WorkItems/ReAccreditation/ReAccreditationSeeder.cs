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
    public const string OrsInterimAuthorityOrganisationName = "Overseas Reprocessing Verification Ltd";

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

        // Newly submitted, no one has picked it up yet.
        yield return Build(
            seedKey: "acme-recycling",
            postcode: "SW1A 1AA",
            submittedDaysAgo: 1,
            stateId: "submitted",
            payload: new BsonDocument
            {
                ["organisationName"] = "Acme Recycling Ltd",
                ["registrationNumber"] = "EPR-100023",
                ["operatorRegistrationId"] = "reg-001",
                ["material"] = "plastic",
                ["previousAccreditationYear"] = 2025,
                ["complianceIssuesReported"] = 0,
                ["operatorEmail"] = "acme.recycling@example.com",
                ["siteAddressPostcode"] = "SW1A 1AA",
                ["chargeAmountPence"] = 54600,
            },
            submittedBy: "stub-portal-client",
            now: now);

        // Submitted and self-claimed by a standard user; first state still
        // has work to do.
        yield return Build(
            seedKey: "northern-plastics",
            postcode: "EH1 3BN",
            submittedDaysAgo: 3,
            stateId: "submitted",
            payload: new BsonDocument
            {
                ["organisationName"] = "Northern Plastics Co-op",
                ["registrationNumber"] = "EPR-100087",
                ["operatorRegistrationId"] = "reg-002",
                ["material"] = "plastic",
                ["previousAccreditationYear"] = 2025,
                ["complianceIssuesReported"] = 1,
                ["operatorEmail"] = "northern.plastics@example.com",
                ["siteAddressPostcode"] = "EH1 3BN",
                ["chargeAmountPence"] = 218400,
            },
            submittedBy: "stub-portal-client",
            assignedToId: "stub-standard-1",
            assignedToName: "Stub Standard User",
            now: now);

        // Mid-assessment: assigned and under active review.
        yield return Build(
            seedKey: "riverside-glass",
            postcode: "CF10 1AA",
            submittedDaysAgo: 9,
            stateId: "assessment-in-progress",
            payload: new BsonDocument
            {
                ["organisationName"] = "Riverside Glass Recovery",
                ["registrationNumber"] = "EPR-099812",
                ["operatorRegistrationId"] = "reg-003",
                ["material"] = "glass",
                ["previousAccreditationYear"] = 2024,
                ["complianceIssuesReported"] = 2,
                ["operatorEmail"] = "riverside.glass@example.com",
                ["siteAddressPostcode"] = "CF10 1AA",
                ["chargeAmountPence"] = 327600,
            },
            submittedBy: "stub-portal-client",
            assignedToId: "stub-assign-1",
            assignedToName: "Stub Assign User",
            now: now);

        // Awaiting decision: parked in the intermediate state a pre-RA-410
        // two-step decision left items in, so the single-call /decision
        // endpoint has a fixture proving it recovers them.
        yield return Build(
            seedKey: "coastal-materials",
            postcode: "BT1 1AA",
            submittedDaysAgo: 15,
            stateId: "awaiting-decision",
            payload: new BsonDocument
            {
                ["organisationName"] = "Coastal Materials Group",
                ["registrationNumber"] = "EPR-098774",
                ["operatorRegistrationId"] = "reg-004",
                ["material"] = "plastic",
                ["previousAccreditationYear"] = 2024,
                ["complianceIssuesReported"] = 0,
                ["operatorEmail"] = "coastal.materials@example.com",
                ["siteAddressPostcode"] = "BT1 1AA",
                ["chargeAmountPence"] = 396500,
            },
            submittedBy: "stub-portal-client",
            assignedToId: "stub-assign-1",
            assignedToName: "Stub Assign User",
            now: now);

        // Already approved — terminal state, useful for exercising the
        // "no further actions" rendering path.
        yield return Build(
            seedKey: "heritage-paper",
            postcode: "BS1 4DJ",
            submittedDaysAgo: 32,
            stateId: "approved",
            payload: new BsonDocument
            {
                ["organisationName"] = "Heritage Paper Mills",
                ["registrationNumber"] = "EPR-097215",
                ["operatorRegistrationId"] = "reg-005",
                ["material"] = "paper",
                ["previousAccreditationYear"] = 2024,
                ["complianceIssuesReported"] = 0,
                ["operatorEmail"] = "heritage.paper@example.com",
                ["siteAddressPostcode"] = "BS1 4DJ",
                ["chargeAmountPence"] = 360400,
            },
            submittedBy: "stub-portal-client",
            assignedToId: "stub-assign-1",
            assignedToName: "Stub Assign User",
            now: now);

        // Additional Scotland item — submitted, unassigned.
        yield return Build(
            seedKey: "clyde-composites",
            postcode: "G1 1AA",
            submittedDaysAgo: 5,
            stateId: "submitted",
            payload: new BsonDocument
            {
                ["organisationName"] = "Clyde Composites Ltd",
                ["registrationNumber"] = "EPR-100134",
                ["operatorRegistrationId"] = "reg-006",
                ["material"] = "plastic",
                ["previousAccreditationYear"] = 2025,
                ["complianceIssuesReported"] = 0,
                ["operatorEmail"] = "clyde.composites@example.com",
                ["siteAddressPostcode"] = "G1 1AA",
                ["chargeAmountPence"] = 54600,
            },
            submittedBy: "stub-portal-client",
            now: now);

        // Additional Wales item — assessment in progress.
        yield return Build(
            seedKey: "swansea-textiles",
            postcode: "SA1 1AA",
            submittedDaysAgo: 11,
            stateId: "assessment-in-progress",
            payload: new BsonDocument
            {
                ["organisationName"] = "Swansea Textiles Recovery",
                ["registrationNumber"] = "EPR-099441",
                ["operatorRegistrationId"] = "reg-007",
                ["material"] = "glass",
                ["previousAccreditationYear"] = 2024,
                ["complianceIssuesReported"] = 1,
                ["operatorEmail"] = "swansea.textiles@example.com",
                ["siteAddressPostcode"] = "SA1 1AA",
                ["chargeAmountPence"] = 218400,
            },
            submittedBy: "stub-portal-client",
            assignedToId: "stub-assign-1",
            assignedToName: "Stub Assign User",
            now: now);

        // Additional Northern Ireland item — submitted, unassigned.
        yield return Build(
            seedKey: "belfast-fibres",
            postcode: "BT7 1AA",
            submittedDaysAgo: 2,
            stateId: "submitted",
            payload: new BsonDocument
            {
                ["organisationName"] = "Belfast Fibres Co",
                ["registrationNumber"] = "EPR-100198",
                ["operatorRegistrationId"] = "reg-008",
                ["material"] = "paper",
                ["previousAccreditationYear"] = 2025,
                ["complianceIssuesReported"] = 0,
                ["operatorEmail"] = "belfast.fibres@example.com",
                ["siteAddressPostcode"] = "BT7 1AA",
                ["chargeAmountPence"] = 396500,
            },
            submittedBy: "stub-portal-client",
            now: now);

        // RA-254: carries every field a real operator submission can send —
        // including submittedBy, prns, businessPlan and samplingPlan, which
        // none of the items above populate. Used by the mgmt-tests e2e suite
        // to verify the Application details page renders the full payload
        // rather than just the subset the other seed items happen to cover.
        yield return Build(
            seedKey: "full-payload-verification",
            postcode: "EC1A 1BB",
            submittedDaysAgo: 4,
            stateId: "submitted",
            payload: new BsonDocument
            {
                ["organisationName"] = "Full Payload Verification Ltd",
                ["registrationNumber"] = "EPR-100999",
                ["operatorApplicationId"] = "app-full-payload-001",
                ["operatorOrganisationId"] = "org-full-payload-001",
                ["operatorRegistrationId"] = "reg-full-payload-001",
                ["material"] = "plastic",
                ["accreditationYear"] = 2026,
                ["previousAccreditationYear"] = 2025,
                ["complianceIssuesReported"] = 0,
                ["operatorEmail"] = "full.payload@example.com",
                ["siteAddress"] = "1 Full Payload Lane, London",
                ["siteAddressPostcode"] = "EC1A 1BB",
                ["chargeAmountPence"] = 327600,
                ["paymentReference"] = "PAY-FULL-PAYLOAD-001",
                ["submittedBy"] = new BsonDocument
                {
                    ["fullName"] = "Priya Sharma",
                    ["jobTitle"] = "Compliance Manager",
                    ["email"] = "priya.sharma@example.com"
                },
                ["prns"] = new BsonDocument
                {
                    ["plannedTonnageBand"] = "UpTo1000",
                    ["authorisers"] = new BsonArray
                    {
                        new BsonDocument
                        {
                            ["fullName"] = "Tom Baker",
                            ["email"] = "tom.baker@example.com"
                        }
                    }
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
                    ["newUsesDetail"] = "Recycled content packaging trial"
                },
                ["samplingPlan"] = new BsonDocument
                {
                    ["files"] = new BsonArray
                    {
                        new BsonDocument
                        {
                            ["fileId"] = "sampling-plan-001",
                            ["filename"] = "sampling-plan.pdf",
                            ["contentType"] = "application/pdf",
                            // A real operator submission sends this field as
                            // plain JSON (System.Text.Json serialises DateTime
                            // as an ISO-8601 string), which lands in Mongo as a
                            // BSON string — not a BsonDateTime. Using a native
                            // DateTime here would round-trip through the API as
                            // `{"$date": "..."}`, which the "Uploaded at" GDS
                            // date filter can't parse (it expects a string).
                            ["uploadedAt"] = "2026-06-01T10:00:00.000Z",
                            ["scanStatus"] = "Clean",
                            // Matches the fixture object seeded into floci's
                            // S3 bucket by the mgmt-tests compose stack
                            // (docker/scripts/localstack/10-setup-buckets.sh),
                            // so the download link this seed item exercises
                            // resolves to a real object end-to-end.
                            ["s3Key"] = "sampling-plans/full-payload-verification/sampling-plan.pdf",
                            ["s3Bucket"] = "epr-register-enrol-sampling-plans"
                        },
                        // RA-295 / AC02: the sampling & inspection plan "could
                        // have other supporting docs and should be listed", so
                        // the fixture needs more than one file — with a single
                        // entry a "lists every document" assertion passes even
                        // against a template that renders files[0] and stops.
                        new BsonDocument
                        {
                            ["fileId"] = "sampling-plan-002",
                            ["filename"] = "sampling-plan-appendix.pdf",
                            ["contentType"] = "application/pdf",
                            ["uploadedAt"] = "2026-06-02T10:00:00.000Z",
                            ["scanStatus"] = "Clean",
                            // Backed by a real object in the mgmt-tests
                            // localstack bucket (seeded by
                            // docker/scripts/localstack/10-setup-buckets.sh in
                            // that repo), so the e2e suite fetches this href
                            // and asserts a 200 + PDF content type. Keep the
                            // two s3Keys distinct: an href bug that serves
                            // file one for both documents is invisible to a
                            // filename-only assertion.
                            ["s3Key"] = "sampling-plans/full-payload-verification/sampling-plan-appendix.pdf",
                            ["s3Bucket"] = "epr-register-enrol-sampling-plans"
                        }
                    }
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
                            ["siteId"] = 1,
                            ["siteName"] = "Full Payload Verification Overseas Site",
                            ["siteAddress"] = "1 Overseas Lane, Rotterdam",
                            ["country"] = "Netherlands",
                            ["besEvidence"] = new BsonDocument
                            {
                                ["files"] = new BsonArray
                                {
                                    new BsonDocument
                                    {
                                        ["fileId"] = "bes-evidence-001",
                                        ["filename"] = "bes-evidence.pdf",
                                        ["contentType"] = "application/pdf",
                                        ["uploadedAt"] = "2026-06-01T10:00:00.000Z",
                                        ["scanStatus"] = "Clean",
                                        ["s3Key"] = "bes-evidence/full-payload-verification/bes-evidence.pdf",
                                        ["s3Bucket"] = "epr-register-enrol-bes-evidence"
                                    }
                                }
                            }
                        }
                    }
                }
            },
            submittedBy: "stub-portal-client",
            now: now);

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
        yield return Build(
            seedKey: OrsInterimAuthoritySeedKey,
            postcode: "EC2A 2BB",
            submittedDaysAgo: 6,
            stateId: "submitted",
            payload: new BsonDocument
            {
                ["organisationName"] = OrsInterimAuthorityOrganisationName,
                ["registrationNumber"] = "EPR-100292",
                ["operatorApplicationId"] = "app-ors-interim-authority-001",
                ["operatorOrganisationId"] = "org-ors-interim-authority-001",
                ["operatorRegistrationId"] = "reg-ors-interim-authority-001",
                ["material"] = "plastic",
                ["accreditationYear"] = 2026,
                ["previousAccreditationYear"] = 2025,
                ["complianceIssuesReported"] = 0,
                ["operatorEmail"] = "ors.verification@example.com",
                ["siteAddress"] = "1 Verification Way, London",
                ["siteAddressPostcode"] = "EC2A 2BB",
                ["chargeAmountPence"] = 218400,
                ["submittedBy"] = new BsonDocument
                {
                    ["fullName"] = "Grace Adeyemi",
                    ["jobTitle"] = "Head of Compliance",
                    ["email"] = "grace.adeyemi@example.com"
                },
                // AC03: authority-to-issue contacts, one of each flag state.
                ["prns"] = new BsonDocument
                {
                    ["plannedTonnageBand"] = "UpTo1000",
                    ["authorisers"] = new BsonArray
                    {
                        new BsonDocument
                        {
                            ["fullName"] = "Grace Adeyemi",
                            ["email"] = "grace.adeyemi@example.com",
                            ["isNew"] = true
                        },
                        new BsonDocument
                        {
                            ["fullName"] = "Martin Cole",
                            ["email"] = "martin.cole@example.com",
                            ["isNew"] = false
                        },
                        // No isNew key — a pre-RA-292 authoriser record.
                        new BsonDocument
                        {
                            ["fullName"] = "Priya Nair",
                            ["email"] = "priya.nair@example.com"
                        }
                    }
                },
                ["overseasSites"] = new BsonDocument
                {
                    ["sites"] = new BsonArray
                    {
                        // AC01 + AC02 positive case, and the AC04 detail set.
                        new BsonDocument
                        {
                            ["siteId"] = 1,
                            ["orsId"] = "ORS-2026-0292",
                            ["siteName"] = "Rotterdam New Reprocessing Site",
                            ["siteAddress"] = "1 Havenstraat, Rotterdam",
                            ["addressLine1"] = "1 Havenstraat",
                            ["addressLine2"] = "Europoort Industrial Park",
                            ["townOrCity"] = "Rotterdam",
                            ["country"] = "Netherlands",
                            ["coordinates"] = "51.9244, 4.4777",
                            ["contactName"] = "Johan de Vries",
                            ["contactEmail"] = "johan.devries@example.com",
                            ["contactPhone"] = "+31 10 123 4567",
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
                            ["isNewSite"] = true,
                            ["registeredNowAccredited"] = false,
                            ["besEvidence"] = new BsonDocument
                            {
                                ["files"] = new BsonArray
                                {
                                    new BsonDocument
                                    {
                                        ["fileId"] = "ra292-bes-evidence-001",
                                        ["filename"] = "bes-evidence.pdf",
                                        ["contentType"] = "application/pdf",
                                        ["uploadedAt"] = "2026-06-01T10:00:00.000Z",
                                        ["scanStatus"] = "Clean",
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
                                        ["s3Key"] = "bes-evidence/full-payload-verification/bes-evidence.pdf",
                                        ["s3Bucket"] = "epr-register-enrol-bes-evidence"
                                    }
                                }
                            },
                            ["interimSite"] = new BsonDocument
                            {
                                ["siteId"] = 11,
                                ["siteNumber"] = "INT-001",
                                ["isNewSite"] = true,
                                ["country"] = "Belgium",
                                ["siteName"] = "Antwerp Interim Holding Site",
                                ["addressLine1"] = "12 Scheldelaan",
                                ["addressLine2"] = "Unit 4",
                                ["townOrCity"] = "Antwerp",
                                ["stateOrRegion"] = "Flanders",
                                ["postcode"] = "2030",
                                ["contactName"] = "Elke Janssens",
                                ["contactEmail"] = "elke.janssens@example.com",
                                ["contactPhone"] = "+32 3 987 6543"
                            }
                        },
                        // AC01 + AC02 negative case: an established site and an
                        // established interim site. Empty besEvidence file
                        // list, and the interim site has no addressLine2.
                        new BsonDocument
                        {
                            ["siteId"] = 2,
                            ["orsId"] = "ORS-2024-0042",
                            ["siteName"] = "Hamburg Established Reprocessing Site",
                            ["siteAddress"] = "42 Hafenstrasse, Hamburg",
                            ["addressLine1"] = "42 Hafenstrasse",
                            ["addressLine2"] = "Building C",
                            ["townOrCity"] = "Hamburg",
                            ["country"] = "Germany",
                            ["coordinates"] = "53.5511, 9.9937",
                            ["contactName"] = "Anna Schmidt",
                            ["contactEmail"] = "anna.schmidt@example.com",
                            ["contactPhone"] = "+49 40 555 0142",
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
                            ["isNewSite"] = false,
                            ["registeredNowAccredited"] = true,
                            // The producer always emits besEvidence with a
                            // files array; a site with no evidence yet sends an
                            // EMPTY array rather than omitting the key. That is
                            // its own rendering branch, distinct from both a
                            // populated list and an absent key.
                            ["besEvidence"] = new BsonDocument
                            {
                                ["files"] = new BsonArray()
                            },
                            ["interimSite"] = new BsonDocument
                            {
                                ["siteId"] = 21,
                                ["siteNumber"] = "INT-002",
                                ["isNewSite"] = false,
                                ["country"] = "Germany",
                                ["siteName"] = "Bremen Interim Storage",
                                ["addressLine1"] = "8 Speicherstrasse",
                                ["townOrCity"] = "Bremen",
                                ["stateOrRegion"] = "Bremen",
                                ["postcode"] = "28217",
                                ["contactName"] = "Lukas Braun",
                                ["contactEmail"] = "lukas.braun@example.com",
                                ["contactPhone"] = "+49 421 555 0188"
                            }
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
                            ["siteId"] = 3,
                            ["orsId"] = "ORS-2023-0007",
                            ["siteName"] = "Bilbao Legacy Reprocessing Site",
                            ["siteAddress"] = "7 Muelle Tomas Olabarri, Bilbao",
                            ["townOrCity"] = "Bilbao",
                            ["country"] = "Spain"
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
                            ["siteId"] = 4,
                            ["orsId"] = "ORS-2025-0113",
                            ["siteName"] = "Port Klang Reprocessing Facility",
                            ["siteAddress"] = "88 Jalan Pelabuhan, Port Klang",
                            ["addressLine1"] = "88 Jalan Pelabuhan",
                            ["addressLine2"] = "Zone 3",
                            ["townOrCity"] = "Port Klang",
                            ["country"] = "Malaysia",
                            ["coordinates"] = "3.0044, 101.3928",
                            ["contactName"] = "Aisyah Rahman",
                            ["contactEmail"] = "aisyah.rahman@example.com",
                            ["contactPhone"] = "+60 3 3168 8000",
                            ["operationCode"] = "R3",
                            ["code1"] = "B3011",
                            ["code2"] = "GH013",
                            ["code3"] = "Y48",
                            ["repatriatedLoads"] = "2",
                            ["isEu"] = false,
                            ["isOecd"] = false,
                            ["isNewSite"] = false,
                            ["registeredNowAccredited"] = false,
                            ["besEvidence"] = new BsonDocument
                            {
                                ["files"] = new BsonArray()
                            }
                        }
                    }
                }
            },
            submittedBy: "stub-portal-client",
            now: now);
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
        string? assignedToName = null)
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
            SlaClock = stateId == SubmittedStateId
                ? null
                : new WorkItemSlaClock { StartedAt = submittedAt.AddDays(1) },
            Payload = payload
        };

        // RA-175: seed a realistic audit trail so the timeline view has
        // plausible history.  Mirrors what WorkItemService.SubmitAsync and
        // the post-submission hooks append for real items.

        // Birth event (mirrors WorkItemService.SubmitAsync).
        workItem.AuditLog.Add(new WorkItemAuditEntry
        {
            Action = "work-item-submitted",
            ActionDisplayName = "Work item submitted",
            Details = new Dictionary<string, string?>
            {
                ["typeId"] = ReAccreditationType.Id,
                ["stateId"] = stateId,
                ["source"] = "seeder",
                ["clientId"] = submittedBy,
                ["applicationReference"] = applicationReference
            },
            CreatedAt = submittedAt,
            CreatedBy = submittedBy,
            CreatedByName = null
        });

        // Nation routing event (mirrors ReAccreditationNationRoutingHook).
        workItem.AuditLog.Add(new WorkItemAuditEntry
        {
            Action = "routed-to-nation",
            ActionDisplayName = "Routed to nation",
            Details = new Dictionary<string, string?>
            {
                ["nation"] = nation.ToString(),
                ["derivedFrom"] = "site-address"
            },
            CreatedAt = submittedAt.AddSeconds(1),
            CreatedBy = null,
            CreatedByName = null
        });

        // Assignment event for assigned items (mirrors WorkItemService.AssignAsync).
        if (assignedToId is not null && assignedAt is not null)
        {
            workItem.AuditLog.Add(new WorkItemAuditEntry
            {
                Action = "assigned",
                ActionDisplayName = "Assigned",
                Details = new Dictionary<string, string?>
                {
                    ["assigneeId"] = assignedToId,
                    ["assigneeName"] = assignedToName,
                    ["previousAssigneeId"] = null,
                    ["previousAssigneeName"] = null
                },
                CreatedAt = assignedAt.Value,
                CreatedBy = SeederAssignedBy,
                CreatedByName = null
            });
        }

        return workItem;
    }

    private static string GenerateDeterministicReference(string seedKey)
    {
        var input = System.Text.Encoding.UTF8.GetBytes(seedKey);
        var hash = System.Security.Cryptography.SHA1.HashData(input);

        // Simple stable uint from first 4 bytes
        uint val = ((uint)hash[0] << 24) | ((uint)hash[1] << 16) | ((uint)hash[2] << 8) | (uint)hash[3];
        var digits = 100_000_000 + (val % 900_000_000);
        return $"RA-{digits}";
    }
}