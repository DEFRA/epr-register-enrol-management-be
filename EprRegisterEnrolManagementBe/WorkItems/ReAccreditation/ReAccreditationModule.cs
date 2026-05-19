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
        services.AddSingleton<IReAccreditationDecisionService, ReAccreditationDecisionService>();
        services.AddSingleton<IWorkItemSeeder, ReAccreditationSeeder>();
        services.AddSingleton<IWorkItemPostActionHook, ReAccreditationNationRoutingHook>();
        services.AddSingleton<IWorkItemPostActionHook, ReAccreditationNotificationHook>();
        // RA-132: accreditation-id generator + module-scoped approval
        // service that owns the bespoke approval workflow (id issuance,
        // SLA clock stop, queued publishing).
        services.AddSingleton<IAccreditationIdGenerator, AccreditationIdGenerator>();
        services.AddSingleton<IReAccreditationApprovalService, ReAccreditationApprovalService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapReAccreditationEndpoints();
    }
}