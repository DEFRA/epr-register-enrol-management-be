using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

namespace EprRegisterEnrolManagementBe.WorkItems.UpdateOverseasSite;

/// <summary>
/// RA-682: registers the ORS update case type, as
/// <see cref="AddOverseasSite.AddOverseasSiteModule"/> does for add-ORS.
/// Submission is the framework's <c>POST /work-items</c> (the type splits a
/// submission per site), and its work items are duly made, determined and
/// closed through the shared Standard-lifecycle routes. It has no endpoints
/// and, being new, no snapshot migrations.
/// </summary>
internal sealed class UpdateOverseasSiteModule : IWorkItemModule
{
    private static readonly UpdateOverseasSiteType s_type = new();

    public IWorkItemType Type => s_type;

    public void RegisterServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddStandardLifecycle<UpdateOverseasSiteLifecycleStrategy>();

    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
}
