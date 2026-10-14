using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;

public sealed class AddOverseasSiteModule : IWorkItemModule
{
    public IWorkItemType Type { get; } = new AddOverseasSiteType();
    public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }
    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
}
