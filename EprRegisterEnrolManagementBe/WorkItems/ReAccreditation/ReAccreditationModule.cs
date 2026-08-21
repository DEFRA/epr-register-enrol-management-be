using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Notifications;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Endpoints;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Self-contained re-accreditation module (RA-98). Wires the type's services
/// and endpoints into the host application; the only change required to
/// "turn the module on" is a single
/// <c>services.AddWorkItemModule&lt;ReAccreditationModule&gt;()</c> in
/// <c>Program.cs</c>.
/// </summary>
internal sealed class ReAccreditationModule : IWorkItemModule
{
    private static readonly ReAccreditationType s_type = new();

    public IWorkItemType Type => s_type;

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<INationResolver, NationResolver>();
        services.AddSingleton<IRegulatorMailboxResolver, RegulatorMailboxResolver>();
        services.AddSingleton<IReAccreditationDecisionService, ReAccreditationDecisionService>();
        services.AddSingleton<IWorkItemSeeder, ReAccreditationSeeder>();
        services.AddSingleton<IWorkItemPostActionHook, ReAccreditationNationRoutingHook>();
        // RA-316: ReAccreditationSlaStampHook is deliberately gone. It restarted
        // the SLA clock at `now` whenever payment-received was applied, which
        // would have wiped the payment-date anchoring duly making now
        // establishes. The clock is started once, by
        // ReAccreditationDulyMakingService, and payment-received no longer
        // touches it.
        // RA-422: outbound CM email + every notification-* audit entry flow
        // through this one hook, so gating its registration on the central
        // Notify:Enabled flag is the single chokepoint for both concerns. When
        // notifications are disabled the hook is never registered, so no email
        // is sent AND no notification-sent/skipped/failed audit entries are
        // written. See NotifyFeature / ConfigureNotifications in Program.cs.
        if (NotifyFeature.NotificationsEnabled(configuration))
        {
            services.AddSingleton<IWorkItemPostActionHook, ReAccreditationNotificationHook>();
        }
        // RA-311/MBE-1: pushes the query note + sections to the operator
        // backend whenever a query is raised.
        services.AddSingleton<IWorkItemPostActionHook, ReAccreditationQueryPushHook>();
        // RA-368: pushes every other state transition to the operator backend.
        // RA-316: no longer needs the extra self-registration it once had —
        // ReAccreditationDulyMakingService reaches it through the ordinary
        // IWorkItemPostActionHook fan-out like every other caller, rather than
        // injecting the concrete type to call it directly.
        services.AddSingleton<IWorkItemPostActionHook, ReAccreditationStatusPushHook>();
        // RA-410: while an item sits in the 'updated' waypoint, report the
        // state the query was raised from — the state it will return to —
        // rather than the waypoint itself. The frontend needs it to decide
        // which call to action to offer and cannot derive it, because it comes
        // from the item's audit history. Purely a projection-time field — no
        // stored field, no template bump, no migration.
        services.AddSingleton<IWorkItemOriginStateResolver, ReAccreditationOriginStateResolver>();
        services.AddSingleton<IWorkItemMigration, ReAccreditationDulyMadeSnapshotMigration>();
        services.AddSingleton<
            IWorkItemMigration,
            ReAccreditationDulyMadeSlaClockBackfillMigration
        >();
        services.AddSingleton<IWorkItemMigration, ReAccreditationMaterialBackfillMigration>();
        // RA-412 (self-review): backfills wasteProcessingType/
        // companyRegisterAddressPostcode on the two overseas-sites seed
        // fixtures for any environment that seeded before those fields were
        // added — CreateIfAbsentAsync never updates an already-seeded id.
        // No ordering dependency on any other migration.
        services.AddSingleton<
            IWorkItemMigration,
            ReAccreditationExporterFixtureBackfillMigration
        >();
        // RA-311/MBE-1: adds the resume-during-* transitions to every
        // existing work item's frozen template snapshot (v6 → v7).
        services.AddSingleton<IWorkItemMigration, ReAccreditationResumeSnapshotMigration>();
        // RA-337: adds the 'updated' state + continue-review-during-*
        // transitions to every existing work item's frozen template
        // snapshot (v7 → v8). Must run after ReAccreditationResumeSnapshotMigration
        // so a v5/v6 item picks up the v7 resume-during-* transitions first.
        services.AddSingleton<IWorkItemMigration, ReAccreditationUpdatedStateSnapshotMigration>();
        // RA-252: adds the withdraw-during-query transition to every
        // existing work item's frozen template snapshot (v8 → v9). No
        // ordering dependency on the migrations above — it only appends a
        // single self-contained transition.
        services.AddSingleton<IWorkItemMigration, ReAccreditationWithdrawQuerySnapshotMigration>();
        // RA-252: adds the withdraw-during-updated transition to every
        // existing work item's frozen template snapshot (v9 → v10). Runs
        // after ReAccreditationWithdrawQuerySnapshotMigration so a v8 (or
        // earlier) item picks up the v9 withdraw-during-query transition
        // first.
        services.AddSingleton<
            IWorkItemMigration,
            ReAccreditationWithdrawUpdatedSnapshotMigration
        >();
        // RA-316: reinstates the duly-make
        // transition on every existing snapshot (v10 → v11). Runs
        // last of the snapshot migrations so an older item has picked up every
        // intervening transition first. Critically, it also partially reverses
        // ReAccreditationDulyMadeSnapshotMigration above — which is why that
        // migration is now version-gated to pre-v5 items, so the two cannot
        // undo each other on every boot.
        services.AddSingleton<IWorkItemMigration, ReAccreditationDulyMakeSnapshotMigration>();
        // RA-410: marks submit-for-decision and reject server-resolved on every
        // existing snapshot (v11 → v12) so an in-flight application stops
        // advertising the old two-step decision path. Runs after the v10 → v11
        // migration above so an older item reaches v11 first.
        services.AddSingleton<IWorkItemMigration, ReAccreditationDecisionSnapshotMigration>();
        // RA-351: adds the queried sla-extend self-loop to every existing
        // work item's frozen template snapshot (v12 → v13). No ordering
        // dependency on the migrations above — it only appends a single
        // self-contained transition, keyed off an sla-extend transition
        // whose from-state is 'queried' (a v12 snapshot already carries the
        // assessment-in-progress sla-extend self-loop).
        services.AddSingleton<IWorkItemMigration, ReAccreditationSlaExtendQuerySnapshotMigration>();
        // epr-2uxy: corrects overseas sites whose frozen isNewSite is a
        // provably-defaulted true. Unlike every migration above it is a no-op
        // until deliberately enabled AND a spot-check is recorded AND apply
        // mode is set — see the type docs for why the gating is this heavy
        // (a bad correction hides a genuinely new site from the regulator,
        // which is worse than the defect it fixes). Safe to register
        // unconditionally: with no configuration it returns immediately.
        services.AddSingleton<IWorkItemMigration, ReAccreditationIsNewSiteCorrectionMigration>();
        // RA-132: module-scoped approval service that owns the bespoke
        // approval workflow (id issuance, SLA clock stop, queued publishing).
        // RA-410: module-scoped service that owns the single-call decision
        // hop. Registered after the approval service it delegates approvals to.
        services.AddSingleton<
            IReAccreditationLogDecisionService,
            ReAccreditationLogDecisionService
        >();
        // RA-448 phase 2: the locally-fabricated accreditation id generator
        // (AccreditationIdGenerator/AccreditationIdLookup) and its
        // ULID-format backfill migration are gone. ReAccreditationApprovalService
        // now resolves a real, backend-issued accreditation number via this
        // adapter instead — registered unconditionally (not gated behind
        // OperatorBackendApi:Enabled like IOperatorBackendPushAdapter's
        // no-op/real switch): unlike that fire-and-forget push, there is no
        // safe no-op for this call, since approval genuinely depends on the
        // number it returns. When the integration is unconfigured, the
        // adapter's own Url check returns a Failure result instead.
        services.AddSingleton<IAccreditationNumberAdapter, HttpAccreditationNumberAdapter>();
        // RA-469 AC16: same unconditional-registration reasoning as
        // IAccreditationNumberAdapter above — a regulator's recycling-
        // operations edit has no safe no-op to fall back to, so the
        // adapter enforces its own Enabled/Url checks rather than being
        // swapped for a no-op implementation when unconfigured.
        services.AddSingleton<
            IOverseasSiteRecyclingOperationsAdapter,
            HttpOverseasSiteRecyclingOperationsAdapter
        >();
        services.AddSingleton<IReAccreditationApprovalService, ReAccreditationApprovalService>();
        // RA-316: bespoke duly-making workflow. Owns the submitted → duly-made
        // transition and the side effect the generic engine cannot perform —
        // anchoring the 12-week SLA clock to the regulator-entered payment date.
        services.AddSingleton<
            IReAccreditationDulyMakingService,
            ReAccreditationDulyMakingService
        >();
        // RA-291: bespoke query workflow (state-derived query-during-* action
        // + query-detail audit entry).
        services.AddSingleton<IReAccreditationQueryService, ReAccreditationQueryService>();
        // RA-311/MBE-1: bespoke resume workflow (state-derived
        // resume-during-* action + query-responded audit entry).
        services.AddSingleton<IReAccreditationResumeService, ReAccreditationResumeService>();
        // RA-252: bespoke withdraw workflow (state-derived
        // withdraw/withdraw-during-* action + withdrawal-reason note).
        services.AddSingleton<IReAccreditationWithdrawService, ReAccreditationWithdrawService>();
        // RA-337: bespoke continue-review workflow (audit-derived
        // continue-review-during-* action) that carries a work item on from
        // 'updated' once a caseworker has reviewed a query resubmission.
        services.AddSingleton<
            IReAccreditationContinueReviewService,
            ReAccreditationContinueReviewService
        >();
        // RA-294/RA-297: thin notification service that appends a
        // 'site-added' audit entry whenever the operator backend reports a
        // new ORS/interim site added to the application. No state
        // transition — this is purely an audit-trail side effect.
        services.AddSingleton<IReAccreditationSiteAddedService, ReAccreditationSiteAddedService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapReAccreditationEndpoints();
    }
}
