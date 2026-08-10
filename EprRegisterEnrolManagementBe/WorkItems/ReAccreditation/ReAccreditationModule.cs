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

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<INationResolver, NationResolver>();
        services.AddSingleton<IRegulatorMailboxResolver, RegulatorMailboxResolver>();
        services.AddSingleton<IReAccreditationDecisionService, ReAccreditationDecisionService>();
        services.AddSingleton<IReAccreditationPaymentService, ReAccreditationPaymentService>();
        services.AddSingleton<IWorkItemSeeder, ReAccreditationSeeder>();
        services.AddSingleton<IWorkItemPostActionHook, ReAccreditationNationRoutingHook>();
        services.AddSingleton<IWorkItemPostActionHook, ReAccreditationSlaStampHook>();
        services.AddSingleton<IWorkItemPostActionHook, ReAccreditationNotificationHook>();
        // RA-311/MBE-1: pushes the query note + sections to the operator
        // backend whenever a query is raised.
        services.AddSingleton<IWorkItemPostActionHook, ReAccreditationQueryPushHook>();
        // RA-368: pushes every other state transition to the operator
        // backend. Registered both as itself and as an IWorkItemPostActionHook
        // (same singleton instance) so ReAccreditationDulyMadeHook can inject
        // it directly to call it explicitly for the submitted→duly-made
        // transition, which bypasses the generic engine's hook fan-out.
        services.AddSingleton<ReAccreditationStatusPushHook>();
        services.AddSingleton<IWorkItemPostActionHook>(sp => sp.GetRequiredService<ReAccreditationStatusPushHook>());
        services.AddSingleton<IWorkItemPostTaskHook, ReAccreditationDulyMadeHook>();
        services.AddSingleton<IWorkItemMigration, ReAccreditationDulyMadeSnapshotMigration>();
        services.AddSingleton<
            IWorkItemMigration,
            ReAccreditationDulyMadeSlaClockBackfillMigration
        >();
        services.AddSingleton<IWorkItemMigration, ReAccreditationMaterialBackfillMigration>();
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
        // epr-2uxy: corrects overseas sites whose frozen isNewSite is a
        // provably-defaulted true. Unlike every migration above it is a no-op
        // until deliberately enabled AND a spot-check is recorded AND apply
        // mode is set — see the type docs for why the gating is this heavy
        // (a bad correction hides a genuinely new site from the regulator,
        // which is worse than the defect it fixes). Safe to register
        // unconditionally: with no configuration it returns immediately.
        services.AddSingleton<
            IWorkItemMigration,
            ReAccreditationIsNewSiteCorrectionMigration
        >();
        // RA-132: accreditation-id generator + module-scoped approval
        // service that owns the bespoke approval workflow (id issuance,
        // SLA clock stop, queued publishing). RA-133: the generator
        // now consults a Mongo-backed lookup for uniqueness.
        services.AddSingleton<IAccreditationIdLookup, AccreditationIdLookup>();
        services.AddSingleton<IAccreditationIdGenerator, AccreditationIdGenerator>();
        services.AddSingleton<IReAccreditationApprovalService, ReAccreditationApprovalService>();
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
