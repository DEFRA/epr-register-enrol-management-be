using Microsoft.AspNetCore.Routing;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// A self-contained work item module. Each work item type lives behind one of
/// these so it can register its own services and HTTP endpoints without the
/// core application needing to know about it. The only change required to add a
/// new type is to call <c>AddWorkItemModule&lt;T&gt;()</c> in <c>Program.cs</c>.
/// </summary>
public interface IWorkItemModule
{
    /// <summary>The type this module owns.</summary>
    IWorkItemType Type { get; }

    /// <summary>
    /// Register the module's service objects with DI. Called once during startup.
    /// Implementations must not register anything that conflicts with another
    /// module's registrations (use module-scoped interfaces).
    /// </summary>
    void RegisterServices(IServiceCollection services);

    /// <summary>
    /// Map the module's HTTP endpoints. Called after the framework has built its
    /// own routes. Modules should mount under a path that includes their TypeId
    /// to stay isolated, e.g. <c>/work-items/re-accreditation/...</c>.
    /// </summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}