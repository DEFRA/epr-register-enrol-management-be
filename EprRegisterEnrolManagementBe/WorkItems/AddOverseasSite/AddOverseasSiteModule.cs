using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

namespace EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;

/// <summary>
/// RA-739: registers the add-ORS case type. Submission (the split into one
/// work item per site) is handled by the framework's <c>POST /work-items</c>
/// because the type declares
/// <see cref="IWorkItemType.SplitsSubmissionPerSite"/>.
///
/// RA-495: the type opts in to the shared Standard-workflow lifecycle
/// (<see cref="AddOverseasSiteLifecycleStrategy"/>), so its work items are
/// duly made, determined and closed through the type-less
/// <c>/work-items/{id}/duly-make|decision|confirm-contact</c> routes. It has
/// no endpoints of its own.
/// </summary>
internal sealed class AddOverseasSiteModule : IWorkItemModule
{
    private static readonly AddOverseasSiteType s_type = new();

    public IWorkItemType Type => s_type;

    public void RegisterServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddStandardLifecycle<AddOverseasSiteLifecycleStrategy>();

    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
}
