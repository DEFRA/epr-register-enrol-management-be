using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;

/// <summary>
/// RA-739: registers the add-ORS case type. Submission (the split into one
/// work item per site) is handled by the framework's <c>POST /work-items</c>
/// because the type declares
/// <see cref="IWorkItemType.UsesSubmissionReference"/>. The module has no
/// services or endpoints of its own yet; RA-495 adds them.
/// </summary>
internal sealed class AddOverseasSiteModule : IWorkItemModule
{
    private static readonly AddOverseasSiteType s_type = new();

    public IWorkItemType Type => s_type;

    public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
}
