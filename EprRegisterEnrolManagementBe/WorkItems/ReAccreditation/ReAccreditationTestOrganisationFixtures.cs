using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;
using EprRegisterEnrolManagementBe.WorkItems.Workflows;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-000: the re-accreditation fixtures the mgmt-tests journey suite
/// cannot create through the API. Both sit on <c>e2e-</c> test organisations
/// and have fixed ids. <see cref="TestOrganisationFixtureHostedService"/>
/// resets them to the documents built here on every boot, on compose
/// (Development demo seeding) and on CDP test (<c>TestOrganisations__Enabled</c>).
/// </summary>
internal sealed class ReAccreditationTestOrganisationFixtures : ITestOrganisationFixtureSeeder
{
    /// <summary>
    /// RA-342 legacy-snapshot fixture id. It is the id the mgmt-tests Mongo
    /// init script (docker/scripts/mongodb/20-legacy-snapshot-work-item.js)
    /// has always used, so the two write the same item.
    /// </summary>
    public const string LegacySnapshotId = "00000342-0000-4000-8000-000000000342";

    /// <summary>Organisation id of the RA-342 fixture.</summary>
    public const string LegacySnapshotOrganisationId = "e2e-ra342-legacy-snapshot";

    /// <summary>Organisation name of the RA-342 fixture.</summary>
    public const string LegacySnapshotOrganisationName = "RA-342 Legacy Snapshot Ltd";

    /// <summary>RA-611 pre-year-start fixture id.</summary>
    public const string PreYearStartId = "00000611-0000-4000-8000-000000000611";

    /// <summary>Organisation id of the RA-611 fixture.</summary>
    public const string PreYearStartOrganisationId = "e2e-ra611-pre-year-start";

    /// <summary>
    /// Organisation name of the RA-611 fixture. Distinct from the demo
    /// seeder's "RA-611 Pre Year Start Ltd" so a name search on a stack
    /// running both finds exactly one of each.
    /// </summary>
    public const string PreYearStartOrganisationName = "E2E RA-611 Pre Year Start Ltd";

    /// <summary>RA-691 accreditation-number-corrected fixture id.</summary>
    public const string AccreditationNumberCorrectedId = "00000691-0000-4000-8000-000000000691";

    /// <summary>Organisation id of the RA-691 fixture.</summary>
    public const string AccreditationNumberCorrectedOrganisationId = "e2e-ra691-accreditation-corrected";

    /// <summary>Organisation name of the RA-691 fixture.</summary>
    public const string AccreditationNumberCorrectedOrganisationName =
        "E2E RA-691 Accreditation Corrected Ltd";

    /// <summary>
    /// RA-691 fixture's number before correction. Real format, on the
    /// fictitious organisation 900691, so it cannot collide with a real
    /// number on the unique <c>payload.accreditationId</c> index.
    /// </summary>
    public const string AccreditationNumberCorrectedFrom = "A27ER9006910322PL";

    /// <summary>RA-691 fixture's number after correction (the stored value).</summary>
    public const string AccreditationNumberCorrectedTo = "A27ER9006910246PL";

    /// <summary>
    /// RA-751 archived fixture id: a <c>withdrawn</c> item whose
    /// <c>payload.archivedAt</c> is set, so the e2e suite can prove the
    /// <c>archived=true</c> filter of <c>GET /work-items</c> returns an
    /// archived item. Only <c>ArchiveBackgroundService</c> stamps that field,
    /// days after an item ends, so no API path can produce one during a run.
    /// </summary>
    public const string ArchivedId = "00000751-0000-4000-8000-000000000751";

    /// <summary>Organisation id of the RA-751 archived fixture.</summary>
    public const string ArchivedOrganisationId = "e2e-ra751-archived";

    /// <summary>Organisation name of the RA-751 archived fixture.</summary>
    public const string ArchivedOrganisationName = "E2E RA-751 Archived Ltd";

    /// <summary>Application reference of the RA-751 archived fixture.</summary>
    public const string ArchivedApplicationReference = "RA-900000751";

    /// <summary>
    /// RA-764 organisation-name-correction fixture id: the case under the
    /// NIEA registration
    /// <see cref="ReAccreditationOrganisationNameCorrectionMigration.NorthernIrelandRegistrationNumber"/>.
    /// </summary>
    public const string OrganisationNameCorrectionId = "00000764-0000-4000-8000-000000000764";

    /// <summary>
    /// RA-764's second fixture id: the case under the EA registration
    /// <see cref="ReAccreditationOrganisationNameCorrectionMigration.EnglandRegistrationNumber"/>,
    /// so the e2e suite proves both registrations' cases are renamed.
    /// </summary>
    public const string OrganisationNameCorrectionEnglandId = "00000764-0000-4000-8000-000000000765";

    /// <summary>
    /// ReEx organisation id of both RA-764 fixtures (one organisation, two
    /// registrations). The fixtures otherwise carry the real target's
    /// identity (org number 500069, its two registration numbers, 2027, the
    /// old name), because the migration is keyed to it; prod never seeds
    /// fixtures.
    /// </summary>
    public const string OrganisationNameCorrectionOrganisationId = "e2e-ra764-organisation-name";

    /// <summary>
    /// Payload field names the fixtures share that have no existing constant
    /// (Sonar S1192; the RA-764 fixture took each to four uses).
    /// </summary>
    private static class Field
    {
        public const string ApplicationReference = "applicationReference";
        public const string OperatorOrganisationId = "operatorOrganisationId";
        public const string Material = "material";
        public const string PreviousAccreditationYear = "previousAccreditationYear";
        public const string ComplianceIssuesReported = "complianceIssuesReported";
        public const string OperatorEmail = "operatorEmail";
        public const string SiteAddressPostcode = "siteAddressPostcode";
        public const string Nation = "nation";
        public const string OperatorApplicationId = "operatorApplicationId";
        public const string SiteAddress = "siteAddress";
        public const string ChargeAmountPence = "chargeAmountPence";
    }

    private const string EnglandNation = "England";
    private const string PlasticMaterial = "plastic";
    private const string FixtureSubmittedBy = "e2e-fixture";

    private const string SimulatedDeliveredEmail = "simulate-delivered@notifications.service.gov.uk";

    public string TypeId => ReAccreditationType.Id;

    public IEnumerable<BsonDocument> Build(IWorkItemType type, TimeProvider time)
    {
        var now = time.GetUtcNow().UtcDateTime;
        yield return BuildLegacySnapshot(now);
        yield return BuildPreYearStart(type, now);
        yield return BuildAccreditationNumberCorrected(type, now);
        yield return BuildArchived(type, now);
        yield return BuildOrganisationNameCorrection(type, now, s_organisationNameCorrectionNorthernIreland);
        yield return BuildOrganisationNameCorrection(type, now, s_organisationNameCorrectionEngland);
    }

    /// <summary>
    /// RA-611: 14 November of the year before <paramref name="now"/>, at
    /// midnight UTC. That is always before 1 January of the current year, so
    /// the deadline floor's 1-January bound (not its duly-made bound) binds
    /// on this fixture, in every year. Midnight UTC matters too: the floor
    /// compares Europe/London dates, and November is GMT, so the UTC date
    /// and the London date are the same.
    /// </summary>
    internal static DateTime PreYearStartClockStart(DateTime now) =>
        new(now.Year - 1, 11, 14, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// RA-690: a valid registration reference for an English plastic
    /// reprocessor registered in the previous accreditation year (both
    /// fixtures' own shape), so it never trips the backend's source-reference
    /// check. <paramref name="orgIdAndSequence"/> is the six-digit org id and
    /// four-digit sequence.
    /// </summary>
    private static string RegistrationReference(DateTime now, string orgIdAndSequence) =>
        $"R{(now.Year - 1) % 100:D2}ER{orgIdAndSequence}PL";

    /// <summary>
    /// RA-342: a <c>submitted</c> item whose frozen snapshot carries elements
    /// the model no longer declares: a top-level <c>retiredSnapshotField</c>,
    /// a state <c>colour</c>, a transition's <c>requiredRoles</c> (the field
    /// RA-323 dropped, named in the original crash) and a task's
    /// <c>mandatory</c>. Before <c>[BsonIgnoreExtraElements]</c> on the
    /// snapshot types, one such document made every <c>GET /work-items</c>
    /// return 500. Built as raw BSON on purpose: going through
    /// <see cref="WorkItem"/> would drop exactly the elements under test.
    /// </summary>
    private static BsonDocument BuildLegacySnapshot(DateTime now) =>
        new()
        {
            ["_id"] = LegacySnapshotId,
            ["typeId"] = ReAccreditationType.Id,
            ["stateId"] = "submitted",
            ["submittedAt"] = now,
            ["lastModifiedAt"] = now,
            ["templateVersion"] = "1",
            ["version"] = 0,
            ["payload"] = new BsonDocument
            {
                [Field.ApplicationReference] = "AP342LEGACY01",
                [ReAccreditationOrganisationNameCorrectionMigration.OrganisationNameField] = LegacySnapshotOrganisationName,
                [Field.OperatorOrganisationId] = LegacySnapshotOrganisationId,
                [ReAccreditationOrganisationNameCorrectionMigration.RegistrationNumberField] = RegistrationReference(now, "9003420342"),
                ["operatorRegistrationId"] = "reg-342",
                [Field.Material] = PlasticMaterial,
                [Field.PreviousAccreditationYear] = now.Year - 1,
                [Field.ComplianceIssuesReported] = 0,
                [Field.OperatorEmail] = SimulatedDeliveredEmail,
                [Field.SiteAddressPostcode] = "SW1A 1AA",
                [Field.Nation] = EnglandNation,
            },
            ["templateSnapshot"] = new BsonDocument
            {
                ["templateVersion"] = "1",
                ["retiredSnapshotField"] = "legacy",
                ["states"] = new BsonArray
                {
                    new BsonDocument
                    {
                        ["_id"] = "submitted",
                        ["displayName"] = "Submitted",
                        ["isTerminal"] = false,
                        ["colour"] = "amber",
                    },
                    new BsonDocument
                    {
                        ["_id"] = "approved",
                        ["displayName"] = "Approved",
                        ["isTerminal"] = true,
                    },
                },
                ["transitions"] = new BsonArray
                {
                    new BsonDocument
                    {
                        ["actionId"] = "approve",
                        ["displayName"] = "Approve",
                        ["fromStateId"] = "submitted",
                        ["toStateId"] = "approved",
                        ["requiresAllTasksComplete"] = true,
                        ["requiredRoles"] = new BsonArray { "regulator-admin", "regulator-approver" },
                    },
                },
                ["tasksByState"] = new BsonDocument
                {
                    ["submitted"] = new BsonArray
                    {
                        new BsonDocument
                        {
                            ["_id"] = "task-one",
                            ["displayName"] = "Task One",
                            ["mandatory"] = true,
                        },
                    },
                },
            },
        };

    /// <summary>
    /// RA-611: an unassigned <c>assessment-in-progress</c> item, carrying the
    /// CURRENT template snapshot so its deadline can be changed through the
    /// UI, whose SLA clock started on <see cref="PreYearStartClockStart"/>.
    /// It is the only item on which a deadline between the clock start and
    /// 1 January is refused with the 1-January message.
    /// </summary>
    private static BsonDocument BuildPreYearStart(IWorkItemType type, DateTime now)
    {
        var clockStart = PreYearStartClockStart(now);
        var submittedAt = clockStart.AddDays(-1);
        var snapshot = WorkItemTemplateSnapshot.Capture(type);
        var item = new WorkItem
        {
            Id = Guid.Parse(PreYearStartId),
            TypeId = ReAccreditationType.Id,
            StateId = "assessment-in-progress",
            SubmittedAt = submittedAt,
            LastModifiedAt = submittedAt,
            SubmittedBy = FixtureSubmittedBy,
            TemplateSnapshot = snapshot,
            TemplateVersion = snapshot.TemplateVersion,
            SlaClock = new WorkItemSlaClock { StartedAt = clockStart },
            Payload = new BsonDocument
            {
                [Field.ApplicationReference] = "RA-900000611",
                [ReAccreditationOrganisationNameCorrectionMigration.OrganisationNameField] = PreYearStartOrganisationName,
                [Field.OperatorOrganisationId] = PreYearStartOrganisationId,
                [Field.OperatorApplicationId] = "e2e-app-ra611-pre-year-start",
                [ReAccreditationOrganisationNameCorrectionMigration.RegistrationNumberField] = RegistrationReference(now, "9006110611"),
                [Field.Material] = PlasticMaterial,
                [AccreditationYear.Field] = now.Year,
                [Field.PreviousAccreditationYear] = now.Year - 1,
                [Field.ComplianceIssuesReported] = 0,
                [Field.OperatorEmail] = SimulatedDeliveredEmail,
                [Field.SiteAddress] = "1 Pre Year Start Road, Leeds",
                [Field.SiteAddressPostcode] = "LS1 4AP",
                [Field.Nation] = EnglandNation,
                [Field.ChargeAmountPence] = 218400,
            },
        };
        return item.ToBsonDocument();
    }

    /// <summary>
    /// RA-691: an <c>approved</c> 2027 item already corrected by
    /// <see cref="ReAccreditationAccreditationNumberCorrectionMigration"/>:
    /// <c>payload.accreditationId</c> holds the corrected number and the audit
    /// log holds the correction entry, built by the migration's own
    /// <see cref="ReAccreditationAccreditationNumberCorrectionMigration.BuildAuditEntry"/>
    /// so the Application history the e2e suite asserts is exactly what the
    /// migration writes. The migration itself is off by default and keyed to
    /// two production numbers, so it never produces this item on a test stack.
    /// </summary>
    private static BsonDocument BuildAccreditationNumberCorrected(IWorkItemType type, DateTime now)
    {
        var submittedAt = now.AddDays(-30);
        var snapshot = WorkItemTemplateSnapshot.Capture(type);
        var item = new WorkItem
        {
            Id = Guid.Parse(AccreditationNumberCorrectedId),
            TypeId = ReAccreditationType.Id,
            StateId = ReAccreditationType.ApprovedStateId,
            SubmittedAt = submittedAt,
            LastModifiedAt = now.AddDays(-1),
            SubmittedBy = FixtureSubmittedBy,
            TemplateSnapshot = snapshot,
            TemplateVersion = snapshot.TemplateVersion,
            Payload = new BsonDocument
            {
                [Field.ApplicationReference] = "RA-900000691",
                [ReAccreditationOrganisationNameCorrectionMigration.OrganisationNameField] = AccreditationNumberCorrectedOrganisationName,
                [Field.OperatorOrganisationId] = AccreditationNumberCorrectedOrganisationId,
                [Field.OperatorApplicationId] = "e2e-app-ra691-accreditation-corrected",
                // RA-690: a valid registration reference for this fixture's fixed
                // 2026 previous accreditation year, sharing the corrected number's
                // org id and sequence.
                [ReAccreditationOrganisationNameCorrectionMigration.RegistrationNumberField] = "R26ER9006910246PL",
                [Field.Material] = PlasticMaterial,
                [AccreditationYear.Field] = 2027,
                [Field.PreviousAccreditationYear] = 2026,
                [Field.ComplianceIssuesReported] = 0,
                [Field.OperatorEmail] = SimulatedDeliveredEmail,
                [Field.SiteAddress] = "1 Corrected Reference Road, Leeds",
                [Field.SiteAddressPostcode] = "LS1 4AP",
                [Field.Nation] = EnglandNation,
                [Field.ChargeAmountPence] = 218400,
                [ReAccreditationAccreditationNumber.PayloadKey] = AccreditationNumberCorrectedTo,
                ["accreditationStartDate"] = "2027-01-01",
            },
            AuditLog =
            [
                ReAccreditationAccreditationNumberCorrectionMigration.BuildAuditEntry(
                    AccreditationNumberCorrectedFrom,
                    AccreditationNumberCorrectedTo,
                    now.AddDays(-1)
                ),
            ],
        };
        return item.ToBsonDocument();
    }

    /// <summary>
    /// RA-751: a <c>withdrawn</c> item ended 10 days ago and archived 3 days
    /// ago, as <c>ArchiveBackgroundService</c> would leave it.
    /// </summary>
    private static BsonDocument BuildArchived(IWorkItemType type, DateTime now)
    {
        var snapshot = WorkItemTemplateSnapshot.Capture(type);
        var item = new WorkItem
        {
            Id = Guid.Parse(ArchivedId),
            TypeId = ReAccreditationType.Id,
            StateId = StandardWorkflow.WithdrawnStateId,
            SubmittedAt = now.AddDays(-40),
            LastModifiedAt = now.AddDays(-10),
            SubmittedBy = FixtureSubmittedBy,
            TemplateSnapshot = snapshot,
            TemplateVersion = snapshot.TemplateVersion,
            Payload = new BsonDocument
            {
                [Field.ApplicationReference] = ArchivedApplicationReference,
                [ReAccreditationOrganisationNameCorrectionMigration.OrganisationNameField] = ArchivedOrganisationName,
                [Field.OperatorOrganisationId] = ArchivedOrganisationId,
                [Field.OperatorApplicationId] = "e2e-app-ra751-archived",
                // A valid registration reference on the fictitious organisation
                // 900751, like the RA-691 fixture's.
                [ReAccreditationOrganisationNameCorrectionMigration.RegistrationNumberField] = "R26ER9007510001PL",
                [Field.Material] = PlasticMaterial,
                [AccreditationYear.Field] = 2027,
                [Field.PreviousAccreditationYear] = 2026,
                [Field.ComplianceIssuesReported] = 0,
                [Field.OperatorEmail] = SimulatedDeliveredEmail,
                [Field.SiteAddress] = "1 Archived Road, Leeds",
                [Field.SiteAddressPostcode] = "LS1 4AP",
                [Field.Nation] = EnglandNation,
                [Field.ChargeAmountPence] = 218400,
                [ArchiveBackgroundService.ArchivedAtPayloadKey] = new BsonDateTime(now.AddDays(-3)),
            },
        };
        return item.ToBsonDocument();
    }

    /// <summary>
    /// What differs between the two RA-764 fixtures. Application reference
    /// and operator application id are distinct because both are unique
    /// (sparse) indexes.
    /// </summary>
    private sealed record OrganisationNameCorrectionCase(
        string Id,
        string ApplicationReference,
        string OperatorApplicationId,
        string RegistrationNumber,
        string SiteAddress,
        string SiteAddressPostcode,
        string Nation
    );

    private static readonly OrganisationNameCorrectionCase s_organisationNameCorrectionNorthernIreland = new(
        OrganisationNameCorrectionId,
        "RA-900000764",
        "e2e-app-ra764-organisation-name",
        ReAccreditationOrganisationNameCorrectionMigration.NorthernIrelandRegistrationNumber,
        "1 Renamed Organisation Road, Belfast",
        "BT1 1AA",
        "NorthernIreland"
    );

    private static readonly OrganisationNameCorrectionCase s_organisationNameCorrectionEngland = new(
        OrganisationNameCorrectionEnglandId,
        "RA-900000765",
        "e2e-app-ra764-organisation-name-ea",
        ReAccreditationOrganisationNameCorrectionMigration.EnglandRegistrationNumber,
        "1 Renamed Organisation Road, Leeds",
        "LS1 1AA",
        EnglandNation
    );

    /// <summary>
    /// RA-764: an unassigned, not-started 2027 glass application for
    /// organisation 500069 under one of its two registrations, carrying its
    /// OLD name, exactly what
    /// <see cref="ReAccreditationOrganisationNameCorrectionMigration"/> renames.
    /// With the migration's keys set (the mgmt-tests compose stack) it is
    /// renamed after every reset; without them (CDP test) it keeps the old
    /// name. No audit entries, so the e2e suite can assert the rename adds
    /// none.
    /// </summary>
    private static BsonDocument BuildOrganisationNameCorrection(
        IWorkItemType type,
        DateTime now,
        OrganisationNameCorrectionCase fixture
    )
    {
        var submittedAt = now.AddDays(-14);
        var snapshot = WorkItemTemplateSnapshot.Capture(type);
        var item = new WorkItem
        {
            Id = Guid.Parse(fixture.Id),
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            SubmittedAt = submittedAt,
            LastModifiedAt = submittedAt,
            SubmittedBy = FixtureSubmittedBy,
            TemplateSnapshot = snapshot,
            TemplateVersion = snapshot.TemplateVersion,
            Payload = new BsonDocument
            {
                [Field.ApplicationReference] = fixture.ApplicationReference,
                [ReAccreditationOrganisationNameCorrectionMigration.OrganisationNameField] = ReAccreditationOrganisationNameCorrectionMigration.OldName,
                [Field.OperatorOrganisationId] = OrganisationNameCorrectionOrganisationId,
                [ReAccreditationOrganisationNameCorrectionMigration.OperatorOrgNumberField] = ReAccreditationOrganisationNameCorrectionMigration.OrganisationNumber,
                [Field.OperatorApplicationId] = fixture.OperatorApplicationId,
                [ReAccreditationOrganisationNameCorrectionMigration.RegistrationNumberField] = fixture.RegistrationNumber,
                [Field.Material] = "glass",
                [AccreditationYear.Field] = ReAccreditationOrganisationNameCorrectionMigration.AccreditationYear,
                [Field.PreviousAccreditationYear] = ReAccreditationOrganisationNameCorrectionMigration.AccreditationYear - 1,
                [Field.ComplianceIssuesReported] = 0,
                [Field.OperatorEmail] = SimulatedDeliveredEmail,
                [Field.SiteAddress] = fixture.SiteAddress,
                [Field.SiteAddressPostcode] = fixture.SiteAddressPostcode,
                [Field.Nation] = fixture.Nation,
                [Field.ChargeAmountPence] = 218400,
            },
        };
        return item.ToBsonDocument();
    }
}
