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
        services.AddSingleton<IWorkItemPostTaskHook, ReAccreditationDulyMadeHook>();
        services.AddSingleton<IWorkItemMigration, ReAccreditationDulyMadeSnapshotMigration>();
        services.AddSingleton<IWorkItemMigration, ReAccreditationDulyMadeSlaClockBackfillMigration>();
        services.AddSingleton<IWorkItemMigration, ReAccreditationMaterialBackfillMigration>();
        // RA-132: accreditation-id generator + module-scoped approval
        // service that owns the bespoke approval workflow (id issuance,
        // SLA clock stop, queued publishing). RA-133: the generator
        // now consults a Mongo-backed lookup for uniqueness.
        services.AddSingleton<IAccreditationIdLookup, AccreditationIdLookup>();
        services.AddSingleton<IAccreditationIdGenerator, AccreditationIdGenerator>();
        services.AddSingleton<IReAccreditationApprovalService, ReAccreditationApprovalService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapReAccreditationEndpoints();
    }
}