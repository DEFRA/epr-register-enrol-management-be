using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

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

    /// <summary>RA-764 organisation-name-correction fixture id.</summary>
    public const string OrganisationNameCorrectionId = "00000764-0000-4000-8000-000000000764";

    /// <summary>
    /// ReEx organisation id of the RA-764 fixture. The fixture otherwise
    /// carries the real target's identity (org number 500069, registration
    /// <see cref="ReAccreditationOrganisationNameCorrectionMigration.RegistrationNumber"/>,
    /// 2027, the old name), because the migration is keyed to it; prod never
    /// seeds fixtures.
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

    private const string SimulatedDeliveredEmail = "simulate-delivered@notifications.service.gov.uk";

    public string TypeId => ReAccreditationType.Id;

    public IEnumerable<BsonDocument> Build(IWorkItemType type, TimeProvider time)
    {
        var now = time.GetUtcNow().UtcDateTime;
        yield return BuildLegacySnapshot(now);
        yield return BuildPreYearStart(type, now);
        yield return BuildAccreditationNumberCorrected(type, now);
        yield return BuildOrganisationNameCorrection(type, now);
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
                [Field.Material] = "plastic",
                [Field.PreviousAccreditationYear] = now.Year - 1,
                [Field.ComplianceIssuesReported] = 0,
                [Field.OperatorEmail] = SimulatedDeliveredEmail,
                [Field.SiteAddressPostcode] = "SW1A 1AA",
                [Field.Nation] = "England",
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
            SubmittedBy = "e2e-fixture",
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
                [Field.Material] = "plastic",
                [AccreditationYear.Field] = now.Year,
                [Field.PreviousAccreditationYear] = now.Year - 1,
                [Field.ComplianceIssuesReported] = 0,
                [Field.OperatorEmail] = SimulatedDeliveredEmail,
                [Field.SiteAddress] = "1 Pre Year Start Road, Leeds",
                [Field.SiteAddressPostcode] = "LS1 4AP",
                [Field.Nation] = "England",
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
            SubmittedBy = "e2e-fixture",
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
                [Field.Material] = "plastic",
                [AccreditationYear.Field] = 2027,
                [Field.PreviousAccreditationYear] = 2026,
                [Field.ComplianceIssuesReported] = 0,
                [Field.OperatorEmail] = SimulatedDeliveredEmail,
                [Field.SiteAddress] = "1 Corrected Reference Road, Leeds",
                [Field.SiteAddressPostcode] = "LS1 4AP",
                [Field.Nation] = "England",
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
    /// RA-764: an unassigned, not-started 2027 glass application for
    /// organisation 500069 carrying its OLD name, exactly what
    /// <see cref="ReAccreditationOrganisationNameCorrectionMigration"/> renames.
    /// With the migration's keys set (the mgmt-tests compose stack) it is
    /// renamed after every reset; without them (CDP test) it keeps the old
    /// name. No audit entries, so the e2e suite can assert the rename adds
    /// none.
    /// </summary>
    private static BsonDocument BuildOrganisationNameCorrection(IWorkItemType type, DateTime now)
    {
        var submittedAt = now.AddDays(-14);
        var snapshot = WorkItemTemplateSnapshot.Capture(type);
        var item = new WorkItem
        {
            Id = Guid.Parse(OrganisationNameCorrectionId),
            TypeId = ReAccreditationType.Id,
            StateId = "submitted",
            SubmittedAt = submittedAt,
            LastModifiedAt = submittedAt,
            SubmittedBy = "e2e-fixture",
            TemplateSnapshot = snapshot,
            TemplateVersion = snapshot.TemplateVersion,
            Payload = new BsonDocument
            {
                [Field.ApplicationReference] = "RA-900000764",
                [ReAccreditationOrganisationNameCorrectionMigration.OrganisationNameField] = ReAccreditationOrganisationNameCorrectionMigration.OldName,
                [Field.OperatorOrganisationId] = OrganisationNameCorrectionOrganisationId,
                [ReAccreditationOrganisationNameCorrectionMigration.OperatorOrgNumberField] = ReAccreditationOrganisationNameCorrectionMigration.OrganisationNumber,
                [Field.OperatorApplicationId] = "e2e-app-ra764-organisation-name",
                [ReAccreditationOrganisationNameCorrectionMigration.RegistrationNumberField] = ReAccreditationOrganisationNameCorrectionMigration.RegistrationNumber,
                [Field.Material] = "glass",
                [AccreditationYear.Field] = ReAccreditationOrganisationNameCorrectionMigration.AccreditationYear,
                [Field.PreviousAccreditationYear] = ReAccreditationOrganisationNameCorrectionMigration.AccreditationYear - 1,
                [Field.ComplianceIssuesReported] = 0,
                [Field.OperatorEmail] = SimulatedDeliveredEmail,
                [Field.SiteAddress] = "1 Renamed Organisation Road, Belfast",
                [Field.SiteAddressPostcode] = "BT1 1AA",
                [Field.Nation] = "NorthernIreland",
                [Field.ChargeAmountPence] = 218400,
            },
        };
        return item.ToBsonDocument();
    }
}
